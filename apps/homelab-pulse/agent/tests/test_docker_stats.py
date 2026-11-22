import json
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import Mock, patch
from uuid import uuid4

from pulse_agent import docker_probe, docker_stats
from pulse_agent.collect import tick
from pulse_agent.config import Config
from pulse_agent.transport import Outcome

from test_docker import AGENT_INI, INSPECT, response


def stats(cache_key="inactive_file"):
    return {
        "cpu_stats": {"online_cpus": 4, "cpu_usage": {"total_usage": 300}, "system_cpu_usage": 1800},
        "precpu_stats": {"cpu_usage": {"total_usage": 200}, "system_cpu_usage": 1000},
        "memory_stats": {"usage": 16384, "stats": {cache_key: 4096}},
    }


def reply(value, status="200"):
    return subprocess.CompletedProcess([], 0, json.dumps(value).encode() + b"\n" + status.encode())


class DockerStatsTests(unittest.TestCase):
    def test_stats_are_sanitized_and_sent_via_the_normal_cycle(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            container_id = uuid4().hex + uuid4().hex
            report = {**stats(), "id": container_id, "name": "/demo-internal"}
            with patch.object(
                docker_probe.subprocess, "run", side_effect=[response({**INSPECT, "id": container_id}), reply(report)],
            ) as run:
                docker_probe.refresh({"worker": "internal-demo-worker"}, root)
            argv = run.call_args.args[0]
            self.assertEqual(argv[argv.index("--unix-socket") + 1], "/run/docker.sock")
            self.assertEqual(argv[-1], f"http://localhost/containers/{container_id}/stats?stream=false")
            self.assertEqual(run.call_args.kwargs["timeout"], 4)
            config = root / "agent.ini"
            config.write_text(AGENT_INI + f"[docker_snapshots]\nworker={root / 'worker.json'}\n")
            transport = Mock()
            transport.send.return_value = Outcome.ACK
            self.assertEqual(tick(Config.load(config), transport), 0)
            payload = transport.send.call_args.args[0]
            reading = json.loads(payload)["values"]["dockerWorkloads"]["worker"]
            self.assertEqual(reading["usage"], {
                "status": "available", "cpuUsageCores": 0.5, "memoryUsageBytes": 12288,
            })
            self.assertNotIn(container_id, payload.decode())
            self.assertNotIn("demo-internal", payload.decode())

    def test_cache_accounting_and_missing_or_reset_cpu(self):
        report = stats("total_inactive_file")
        report["cpu_stats"]["cpu_usage"]["total_usage"] = 10
        with patch.object(docker_stats.subprocess, "run", return_value=reply(report)):
            self.assertEqual(docker_stats.read_usage("f" * 64), {
                "status": "available", "memoryUsageBytes": 12288,
            })
        report["cpu_stats"] = {"cpu_usage": {"total_usage": 200, "percpu_usage": [100, 100]}, "system_cpu_usage": 1800}
        report["memory_stats"]["stats"] = {}
        with patch.object(docker_stats.subprocess, "run", return_value=reply(report)):
            self.assertEqual(docker_stats.read_usage("f" * 64), {
                "status": "available", "cpuUsageCores": 0, "memoryUsageBytes": 16384,
            })

    def test_bad_responses_timeouts_and_invalid_identity_are_unavailable(self):
        for result in (reply({}, "302"), reply({}), reply([]), reply({"raw": "x" * 65537})):
            with self.subTest(result_size=len(result.stdout)), patch.object(
                docker_stats.subprocess, "run", return_value=result,
            ):
                self.assertEqual(docker_stats.read_usage("f" * 64), {"status": "unavailable"})
        with patch.object(docker_stats.subprocess, "run", side_effect=subprocess.TimeoutExpired([], 4)) as run:
            self.assertEqual(docker_stats.read_usage("not-an-id"), {"status": "unavailable"})
            run.assert_not_called()
            self.assertEqual(docker_stats.read_usage("f" * 64), {"status": "unavailable"})
