import json
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import Mock, patch

from pulse_agent import docker_volume_probe
from pulse_agent.collect import tick
from pulse_agent.collectors.docker import collect_volumes
from pulse_agent.config import Config
from pulse_agent.transport import Outcome


class DockerVolumeTests(unittest.TestCase):
    def test_local_directory_reaches_outbox_as_sanitized_volume_measurement(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            volume = root / "private-volume"
            volume.mkdir()
            (volume / "data").write_bytes(b"x" * 8192)
            docker_volume_probe.refresh({"data": str(volume), "missing": str(root / "missing")}, root)
            config_path = root / "agent.ini"
            config_path.write_text(
                "[agent]\ningest_url=http://localhost/internal/v1/telemetry\n"
                f"[docker_volume_snapshots]\ndata={root / 'data.json'}\nmissing={root / 'missing.json'}\n"
            )
            transport = Mock()
            transport.send.side_effect = (Outcome.RETRY, Outcome.ACK, Outcome.ACK)
            config = Config.load(config_path)
            self.assertEqual(tick(config, transport), 1)
            self.assertEqual(tick(config, transport), 0)
            first, second, replay = (call.args[0] for call in transport.send.call_args_list)
            self.assertEqual(first, replay)
            volumes = json.loads(second)["values"]["dockerVolumes"]
            self.assertEqual(volumes["data"]["status"], "available")
            self.assertGreater(volumes["data"]["usedBytes"], 0)
            self.assertGreater(volumes["data"]["filesystemCapacityBytes"], 0)
            self.assertLessEqual(volumes["data"]["filesystemAvailableBytes"], volumes["data"]["filesystemCapacityBytes"])
            self.assertEqual(volumes["missing"]["status"], "unavailable")
            self.assertNotIn(b"private-volume", second)

    def test_partial_stale_and_invalid_readings(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with patch.object(docker_volume_probe, "_run", side_effect=[b"4096 100 50\n", b""]):
                docker_volume_probe.refresh({"data": str(root)}, root)
            path = root / "data.json"
            reading = json.loads(path.read_bytes())
            self.assertEqual(
                reading, {"status": "available", "observedAt": reading["observedAt"],
                          "filesystemCapacityBytes": 409600, "filesystemAvailableBytes": 204800},
            )
            then = datetime.now(timezone.utc) - timedelta(minutes=16)
            reading["observedAt"] = then.isoformat(timespec="seconds").replace("+00:00", "Z")
            path.write_text(json.dumps({**reading, "source": "/demo/private"}))
            self.assertEqual(collect_volumes({"data": str(path)}), {
                "data": {"status": "stale", "observedAt": reading["observedAt"]},
            })
            path.write_text("[]")
            self.assertEqual(collect_volumes({"data": str(path)}), {"data": {"status": "unavailable"}})

    def test_input_rejects_noncanonical_paths_and_skips_symlink(self):
        for value in ("relative", "/", "/demo/../other", "/demo\npath"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                docker_volume_probe.refresh({"data": value}, Path("/fixture"))
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            link = root / "link"
            link.symlink_to(root)
            with patch.object(docker_volume_probe, "_run") as run:
                self.assertEqual(docker_volume_probe._probe(str(link)), {})
                run.assert_not_called()
