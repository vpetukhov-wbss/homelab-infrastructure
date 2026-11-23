import json
import os
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import Mock, patch

from pulse_agent.collect import tick
from pulse_agent.collectors import nginx_access
from pulse_agent.config import Config
from pulse_agent.transport import Outcome

INI = "[agent]\ningest_url=http://localhost/internal/v1/telemetry\n"


class NginxAccessTests(unittest.TestCase):
    def setUp(self):
        root = tempfile.TemporaryDirectory()
        self.addCleanup(root.cleanup)
        self.root = Path(root.name)
        self.log = self.root / "access.log"
        self.ini = self.root / "agent.ini"
        self.ini.write_text(INI + f"nginx_access_log={self.log}\n")
        self.now = datetime.now(timezone.utc)

    def line(self, status=200, age=30, suffix=""):
        tz = timezone(timedelta(hours=2))
        stamp = (self.now - timedelta(seconds=age)).astimezone(tz).strftime("%d/%b/%Y:%H:%M:%S %z")
        return f'192.0.2.1 - - [{stamp}] "GET /demo?example=discard HTTP/1.1" {status} 42 "-" "fixture"{suffix}\n'

    def test_counts_and_latency_go_through_delivery_and_retry(self):
        self.log.write_text(
            self.line(suffix=" rt=0.004") + self.line(503, suffix=" rt=1.500")
            + self.line(502, age=600) + self.line(201, age=-90) + "partial"
        )
        config = Config.load(self.ini)
        transport = Mock()
        transport.send.side_effect = [Outcome.RETRY, Outcome.ACK, Outcome.ACK]
        self.assertEqual(tick(config, transport), 1)
        self.assertEqual(tick(config, transport), 0)
        first, second, replay = [call.args[0] for call in transport.send.call_args_list]
        self.assertEqual(first, replay)
        reading = json.loads(first)["values"]["nginxAccess"]
        self.assertEqual(reading, json.loads(second)["values"]["nginxAccess"])
        self.assertEqual(reading, {
            "status": "available", "observedAt": reading["observedAt"], "windowSeconds": 300,
            "requests": 2, "serverErrors": 1, "tailTruncated": False, "requestTimeP95Ms": 1500,
        })

    def test_legacy_format_reports_counts_and_omits_incomplete_latency(self):
        self.log.write_text(self.line(404) + self.line(500, suffix=" rt=0.007"))
        reading = nginx_access.collect(self.log, None)
        self.assertEqual((reading["requests"], reading["serverErrors"]), (2, 1))
        self.assertNotIn("requestTimeP95Ms", reading)

    def test_truncated_tail_counts_only_complete_recent_records(self):
        recent = self.line(502, suffix=" rt=0.010")
        self.log.write_text(self.line(501, suffix=" rt=0.010") + recent)
        for margin in (0, 8):
            with patch.object(nginx_access, "MAX_LOG_BYTES", len(recent) + margin):
                reading = nginx_access.collect(self.log, None)
            self.assertEqual((reading["requests"], reading["serverErrors"]), (1, 1))
            self.assertTrue(reading["tailTruncated"])
            self.assertNotIn("requestTimeP95Ms", reading)

    def test_empty_old_missing_non_regular_and_unrecognized_sources(self):
        for content in ("", self.line(age=600)):
            self.log.write_text(content)
            reading = nginx_access.collect(self.log, None)
            self.assertEqual((reading["requests"], reading["serverErrors"]), (0, 0))
        self.log.write_text("unexpected format\n")
        fifo = self.root / "log-fifo"
        os.mkfifo(fifo)
        link = self.root / "log-link"
        link.symlink_to(self.log)
        for path in (self.log, fifo, link, self.root, self.root / "missing"):
            with self.subTest(path=path):
                reading = nginx_access.collect(path, None)
                self.assertEqual(reading, {"status": "unavailable", "observedAt": reading["observedAt"]})

    def test_relative_path_is_rejected(self):
        self.ini.write_text(INI + "nginx_access_log=relative.log\n")
        with self.assertRaisesRegex(ValueError, "NGINX access log path"):
            Config.load(self.ini)
