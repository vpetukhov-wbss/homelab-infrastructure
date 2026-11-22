import json
import tempfile
import threading
import unittest
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
from unittest.mock import Mock, patch

from pulse_agent.collect import tick
from pulse_agent.collectors import gitea
from pulse_agent.config import Config
from pulse_agent.transport import Outcome

AGENT_INI = "[agent]\ningest_url=http://localhost/internal/v1/telemetry\n"


def health(database="pass", cache="pass"):
    return json.dumps({
        "status": "pass" if database == cache == "pass" else "fail",
        "description": "synthetic-internal-forge",
        "checks": {
            "database:ping": [{"status": database, "output": "discard-this"}],
            "cache:ping": [{"status": cache}],
        },
    }).encode()


class HealthFixture(BaseHTTPRequestHandler):
    def do_GET(self):
        self.server.requests.append((self.path, self.headers.get("Authorization")))
        self.send_response(self.server.status)
        if self.server.status == 302:
            self.send_header("Location", "/unapproved")
        self.end_headers()
        if self.server.status in (200, 424):
            self.wfile.write(self.server.body)

    def log_message(self, *args):
        pass


class GiteaTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.server = HTTPServer(("127.0.0.1", 0), HealthFixture)
        self.addCleanup(self.server.server_close)
        self.server.requests = []
        self.server.status, self.server.body = 200, health()
        thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(thread.join)
        self.addCleanup(self.server.shutdown)
        self.root = Path(self.directory.name)
        self.url = f"http://127.0.0.1:{self.server.server_port}/api/healthz"

    def test_tick_sends_a_sanitized_reading_and_retries_the_same_payload(self):
        path = self.root / "agent.ini"
        path.write_text(AGENT_INI + f"gitea_health_url={self.url}\n")
        config = Config.load(path)
        transport = Mock()
        transport.send.side_effect = (Outcome.RETRY, Outcome.ACK, Outcome.ACK, Outcome.ACK, Outcome.ACK)

        self.assertEqual(tick(config, transport), 1)
        self.assertEqual(tick(config, transport), 0)
        first, second, replay = [call.args[0] for call in transport.send.call_args_list]
        self.assertEqual(first, replay)
        reading = json.loads(first)["values"]["gitea"]
        self.assertEqual(reading, json.loads(second)["values"]["gitea"])
        self.assertGreaterEqual(reading["responseTimeMs"], 0)
        self.assertEqual(reading, {
            "status": "available", "observedAt": reading["observedAt"],
            "databaseHealthy": True, "cacheHealthy": True, "responseTimeMs": reading["responseTimeMs"],
        })
        self.assertEqual(self.server.requests, [("/api/healthz", None)])

        cache = config.state_directory / "gitea-health.json"
        self.server.status, self.server.body = 424, health(cache="fail")
        self.expire(cache)
        self.assertEqual(tick(config, transport), 0)
        failed = json.loads(transport.send.call_args.args[0])["values"]["gitea"]
        self.assertEqual(failed["status"], "available")
        self.assertIs(failed["databaseHealthy"], True)
        self.assertIs(failed["cacheHealthy"], False)

        self.expire(cache)
        self.server.status = 503
        self.assertEqual(tick(config, transport), 0)
        values = json.loads(transport.send.call_args.args[0])["values"]
        self.assertEqual(values["gitea"], {
            "status": "unavailable", "observedAt": values["gitea"]["observedAt"],
        })
        self.assertIn("cpuTimesSeconds", values)
        self.assertEqual(len(self.server.requests), 3)

    def expire(self, cache):
        stored = json.loads(cache.read_text())
        expired = datetime.now(timezone.utc) - timedelta(seconds=31)
        stored["reading"]["observedAt"] = expired.isoformat().replace("+00:00", "Z")
        cache.write_text(json.dumps(stored))

    def test_bad_responses_and_redirects_are_unavailable(self):
        for status, body in (
            (302, b""), (200, b"{"), (200, b"[]"), (200, b'{"status":"pass"}'),
            (424, health()), (200, health(database="fail")),
            (200, b" " * (gitea.MAX_RESPONSE_BYTES + 1)),
        ):
            with self.subTest(status=status, size=len(body)):
                self.server.status, self.server.body = status, body
                count = len(self.server.requests)
                self.assertEqual(gitea.collect(self.url, None)["status"], "unavailable")
                self.assertEqual(self.server.requests[count:], [("/api/healthz", None)])

    def test_socket_timeout_is_unavailable(self):
        with patch.object(gitea, "build_opener") as factory:
            factory.return_value.open.side_effect = TimeoutError
            self.assertEqual(gitea.collect(self.url, None)["status"], "unavailable")
            self.assertEqual(factory.return_value.open.call_args.kwargs["timeout"], 1)

    def test_config_accepts_private_health_only(self):
        path = self.root / "agent.ini"
        path.write_text(AGENT_INI)
        self.assertIsNone(Config.load(path).gitea_health_url)
        path.write_text(AGENT_INI + "gitea_health_url=https://[::1]:4000/git/api/healthz\n")
        self.assertEqual(Config.load(path).gitea_health_url, "https://[::1]:4000/git/api/healthz")
        for url in (
            "http://192.0.2.1/api/healthz", "http://forge.example.invalid/api/healthz",
            "http://127.0.0.1/metrics", "http://127.0.0.1/api/healthz?extra=true",
            "http://example@127.0.0.1/api/healthz",
        ):
            with self.subTest(url=url), self.assertRaises(ValueError):
                path.write_text(AGENT_INI + f"gitea_health_url={url}\n")
                Config.load(path)
