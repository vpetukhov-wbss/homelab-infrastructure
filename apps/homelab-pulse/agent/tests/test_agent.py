import json
import logging
import os
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from unittest.mock import patch
from uuid import uuid4

from pulse_agent.collect import tick
from pulse_agent.collectors import host
from pulse_agent.config import Config, read_token
from pulse_agent.models import Sample
from pulse_agent.outbox import Outbox
from pulse_agent.transport import Outcome, Transport

OFFLINE_TOKEN = str(uuid4())


class AgentTests(unittest.TestCase):
    def test_host_sample_uses_explicit_aliases_and_units(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp)
            proc = path / "proc"
            (proc / "sys/kernel/random").mkdir(parents=True)
            (proc / "sys/kernel/random/boot_id").write_text(
                "00000000-0000-4000-8000-000000000001\n"
            )
            (proc / "stat").write_text("cpu 100 2 40 300 5 6 7 8 9 10\n")
            (proc / "meminfo").write_text(
                "MemTotal: 8192 kB\nMemAvailable: 4096 kB\nSwapTotal: 64 kB\nSwapFree: 32 kB\n"
            )
            (proc / "loadavg").write_text("0.4 0.3 0.2 1/99 42\n")
            (proc / "uptime").write_text("123.5 15.2\n")
            sensor = path / "temp1_input"
            sensor.write_text("62300\n")
            config = Config(
                "http://localhost/internal/v1/telemetry", path, 1, 4, 60, 4,
                {"root": str(path)}, {}, {"cpu": str(sensor), "missing": str(path / "none")},
            )
            with self.assertLogs("pulse_agent.collectors.host", logging.WARNING):
                values = host.collect(config, proc=proc)

        self.assertAlmostEqual(values["cpuTimesSeconds"]["user"], 100 / os.sysconf("SC_CLK_TCK"))
        self.assertEqual(values["memory"]["availableBytes"], 4096 * 1024)
        self.assertEqual(values["temperaturesCelsius"], {"cpu": 62.3})
        self.assertIn("root", values["filesystems"])

    def test_delivery_replays_same_sample_id_after_failure(self):
        statuses = [503, 204, 204]
        received = []

        class Receiver(BaseHTTPRequestHandler):
            def do_POST(self):
                self.server.received.append((
                    self.path, self.headers.get("Authorization"),
                    json.loads(self.rfile.read(int(self.headers["Content-Length"]))),
                ))
                self.send_response(self.server.statuses.pop(0))
                self.end_headers()

            def log_message(self, *args):
                pass

        with ThreadingHTTPServer(("127.0.0.1", 0), Receiver) as server, tempfile.TemporaryDirectory() as temp:
            server.received, server.statuses = received, statuses
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                state = Path(temp) / "queue"
                url = f"http://127.0.0.1:{server.server_port}/internal/v1/telemetry"
                config = Config(url, state, 1, 10, 60, 4, {}, {}, {})
                sender = Transport(url, OFFLINE_TOKEN, 1)
                with patch("pulse_agent.collect.host.collect", return_value={"fixture": True}):
                    with self.assertLogs("pulse_agent.collect", logging.WARNING):
                        self.assertEqual(tick(config, sender), 1)
                    self.assertEqual(len(Outbox(state).pending()), 1)
                    self.assertEqual(tick(config, sender), 0)
                    self.assertEqual(Outbox(state).pending(), [])
            finally:
                server.shutdown()
                thread.join()

        self.assertEqual(received[0][0], "/internal/v1/telemetry")
        self.assertEqual(received[0][1], f"Bearer {OFFLINE_TOKEN}")
        self.assertEqual(received[0][2]["sampleId"], received[2][2]["sampleId"])
        self.assertNotEqual(received[0][2]["sampleId"], received[1][2]["sampleId"])

    def test_delivery_does_not_forward_credentials_to_redirect(self):
        hits = []

        class Redirect(BaseHTTPRequestHandler):
            def do_POST(self):
                self.send_response(302)
                self.send_header("Location", "/different")
                self.end_headers()

            def do_GET(self):
                hits.append(self.headers.get("Authorization"))
                self.send_response(204)
                self.end_headers()

            def log_message(self, *args):
                pass

        with ThreadingHTTPServer(("127.0.0.1", 0), Redirect) as server:
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                url = f"http://127.0.0.1:{server.server_port}/internal/v1/telemetry"
                self.assertEqual(Transport(url, OFFLINE_TOKEN, 1).send(b"{}"), Outcome.RETRY)
            finally:
                server.shutdown()
                thread.join()
        self.assertEqual(hits, [])

    def test_systemd_credential_file_is_used(self):
        with tempfile.TemporaryDirectory() as temp:
            (Path(temp) / "ingest-token").write_text(OFFLINE_TOKEN + "\n")
            with patch.dict(os.environ, {"CREDENTIALS_DIRECTORY": temp}):
                self.assertEqual(read_token(None), OFFLINE_TOKEN)

    def test_outbox_keeps_the_latest_samples_within_retention(self):
        with tempfile.TemporaryDirectory() as temp:
            queue = Outbox(Path(temp))
            samples = [Sample.host({}) for _ in range(5)]
            base = 1_000_000_000_000
            stamps = [base - 62 * 10**9, base - 10, base - 9, base - 8, base - 7]
            with patch("pulse_agent.outbox.time.time_ns", side_effect=stamps):
                for sample in samples:
                    queue.enqueue(sample)
            with patch("pulse_agent.outbox.time.time_ns", return_value=base):
                self.assertEqual(queue.prune(max_pending=3, max_age=60), 2)
                self.assertEqual(queue.prune(max_pending=10, max_age=0), 3)
            self.assertEqual(queue.pending(), [])
