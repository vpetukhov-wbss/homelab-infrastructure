import json
import stat
import subprocess
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import Mock, patch

from pulse_agent import docker_probe
from pulse_agent.collect import tick
from pulse_agent.collectors import docker
from pulse_agent.config import Config
from pulse_agent.transport import Outcome

AGENT_INI = "[agent]\ningest_url=http://localhost/internal/v1/telemetry\n"
INSPECT = {
    "name": "/internal-demo-worker", "state": "running", "health": "unhealthy",
    "restartCount": 3, "restartPolicy": "unless-stopped", "memoryLimitBytes": 268435456,
    "nanoCpus": 1500000000, "cpuPeriod": 0, "cpuQuota": 0,
    "internalDiagnostic": "synthetic health output",
}
METRICS = {
    "state": "running", "health": "unhealthy", "restartCount": 3, "restartPolicy": "unless-stopped",
    "memoryLimitBytes": 268435456, "cpuLimitCores": 1.5,
}


def response(data, code=0):
    return subprocess.CompletedProcess([], code, json.dumps(data).encode())


class DockerProbeTests(unittest.TestCase):
    def test_managed_producer_feeds_sanitized_snapshots_through_tick_and_outbox(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            inspect = {**INSPECT, "mounts": [
                {"name": "demo-data", "rw": False},
                {"name": "demo-cache", "rw": True},
                {"name": "demo-cache", "rw": False},
                {"name": "unapproved", "rw": True, "source": "/demo/private"},
            ]}
            with patch.object(docker_probe.subprocess, "run", return_value=response(inspect)) as run:
                docker_probe.refresh(
                    {"worker": "internal-demo-worker"}, root, volumes={"data": "demo-data", "cache": "demo-cache"},
                )
            argv = run.call_args.args[0]
            self.assertEqual(argv, [
                "/usr/bin/docker", "--host=unix:///run/docker.sock", "container",
                "inspect", "--format", docker_probe.VOLUME_FORMAT, "--", "internal-demo-worker",
            ])
            self.assertEqual(run.call_args.kwargs["timeout"], 2)
            self.assertEqual(run.call_args.kwargs["stderr"], subprocess.DEVNULL)

            snapshot = root / "worker.json"
            self.assertEqual(stat.S_IMODE(snapshot.stat().st_mode), 0o640)
            reading = json.loads(snapshot.read_text())
            self.assertEqual(reading, {
                "status": "available", "observedAt": reading["observedAt"], **METRICS,
                "volumeAttachments": {"data": "ro", "cache": "mixed"},
            })
            config = root / "agent.ini"
            config.write_text(AGENT_INI + f"[docker_snapshots]\nworker={snapshot}\n")
            transport = Mock()
            transport.send.side_effect = (Outcome.RETRY, Outcome.ACK, Outcome.ACK, Outcome.ACK)
            self.assertEqual(tick(Config.load(config), transport), 1)
            self.assertEqual(tick(Config.load(config), transport), 0)
            first, second, replay = (call.args[0] for call in transport.send.call_args_list)
            self.assertEqual(first, replay)
            for payload in (first, second):
                self.assertEqual(json.loads(payload)["values"]["dockerWorkloads"], {"worker": reading})

            with patch.object(docker_probe.subprocess, "run", side_effect=subprocess.TimeoutExpired(argv, 2)):
                docker_probe.refresh({"worker": "internal-demo-worker"}, root)
            self.assertEqual(tick(Config.load(config), transport), 0)
            values = json.loads(transport.send.call_args.args[0])["values"]
            failed = values["dockerWorkloads"]["worker"]
            self.assertEqual(failed, {"status": "unavailable", "observedAt": failed["observedAt"]})
            self.assertIn("cpuTimesSeconds", values)

    def test_runtime_states_optional_health_and_limits(self):
        stopped = {
            **INSPECT, "state": "exited", "health": None, "restartPolicy": "", "memoryLimitBytes": 0,
            "nanoCpus": 0, "cpuQuota": 25000, "cpuPeriod": 50000,
        }
        with patch.object(docker_probe.subprocess, "run", return_value=response(stopped)):
            self.assertEqual(docker_probe._probe("internal-demo-worker"), {
                "state": "exited", "restartCount": 3, "restartPolicy": "no", "cpuLimitCores": 0.5,
            })
        for invalid in (
            response({**INSPECT, "name": "/other-container"}),
            response({**INSPECT, "restartCount": True}),
            response(INSPECT, code=1),
            subprocess.CompletedProcess([], 0, b"x" * (docker.MAX_SNAPSHOT_CHARS + 1)),
        ):
            with self.subTest(result=invalid), patch.object(docker_probe.subprocess, "run", return_value=invalid):
                self.assertEqual(docker_probe._probe("internal-demo-worker"), {})

    def test_snapshot_age_malformed_content_and_allowlist(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "worker.json"
            files = {"worker": str(path)}
            self.assertEqual(docker.collect(files), {"worker": {"status": "unavailable"}})
            now = datetime.now(timezone.utc)
            for offset, expected in ((10, "unavailable"), (-10, "available"), (-181, "stale")):
                with self.subTest(offset=offset):
                    observed = (now + timedelta(seconds=offset)).isoformat(timespec="seconds").replace("+00:00", "Z")
                    path.write_text(json.dumps({
                        "status": "available", "observedAt": observed, **METRICS, "path": "/demo/private",
                    }))
                    reading = docker.collect(files)["worker"]
                    self.assertEqual(reading["status"], expected)
                    self.assertNotIn("path", reading)
                    if expected == "stale":
                        self.assertEqual(reading, {"status": "stale", "observedAt": observed})
            path.write_text("[]")
            self.assertEqual(docker.collect(files), {"worker": {"status": "unavailable"}})
            config = Path(temp) / "agent.ini"
            config.write_text(AGENT_INI + "[docker_snapshots]\nworker=relative.json\n")
            with self.assertRaisesRegex(ValueError, "Docker snapshot paths"):
                Config.load(config)

    def test_mapping_rejects_invalid_aliases_names_or_size_before_probing(self):
        for mapping in (
            {"../worker": "demo"}, {"worker": "--help"},
            {"worker": "demo/another"}, {f"demo-{index}": "demo" for index in range(9)},
        ):
            with self.subTest(mapping=mapping), patch.object(docker_probe, "_probe") as probe:
                with self.assertRaises(ValueError):
                    docker_probe.refresh(mapping, Path("/fixture"))
                probe.assert_not_called()

    def test_cli_reads_current_and_legacy_mappings(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            path = root / "config.json"
            path.write_text(json.dumps({"containers": {"worker": "demo"}, "volumes": {"data": "demo-data"}}))
            with patch("sys.argv", ["probe", str(path), str(root)]), patch.object(docker_probe, "refresh") as refresh:
                self.assertEqual(docker_probe.main(), 0)
                refresh.assert_called_once_with({"worker": "demo"}, root, volumes={"data": "demo-data"})
                refresh.reset_mock()
                path.write_text(json.dumps({"containers": "demo"}))
                self.assertEqual(docker_probe.main(), 0)
                refresh.assert_called_once_with({"containers": "demo"}, root)

    def test_attachment_field_distinguishes_no_approved_mounts_from_disabled_source(self):
        report = {**INSPECT, "mounts": [{"name": "other", "rw": True}]}
        with patch.object(docker_probe.subprocess, "run", return_value=response(report)):
            self.assertEqual(docker_probe._probe("internal-demo-worker", {"demo-data": "data"}), {
                **METRICS, "volumeAttachments": {},
            })
            self.assertEqual(docker_probe._probe("internal-demo-worker"), METRICS)
