import json
import tempfile
import threading
import unittest
from base64 import b64encode
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
from unittest.mock import Mock, patch
from uuid import uuid4

from pulse_agent.collect import tick
from pulse_agent.collectors import rabbitmq
from pulse_agent.config import Config, RabbitMQSettings
from pulse_agent.transport import Outcome

AGENT_INI = "[agent]\ningest_url=http://localhost/internal/v1/telemetry\n"
COUNTER_QUERY = "?columns=messages,messages_ready,messages_unacknowledged,consumers"


class QueueFixture(BaseHTTPRequestHandler):
    def do_GET(self):
        auth = self.headers.get("Authorization")
        self.server.requests.append((self.command, self.path, auth))
        status = self.server.status if auth == self.server.auth else 401
        self.send_response(status)
        if status == 302:
            self.send_header("Location", "/unapproved")
        self.end_headers()
        if status == 200:
            self.wfile.write(self.server.body)

    def log_message(self, *args):
        pass


class RabbitMQTests(unittest.TestCase):
    def test_tick_delivers_filtered_queue_readings_and_reprobes_after_expiry(self):
        with tempfile.TemporaryDirectory() as temp, HTTPServer(("127.0.0.1", 0), QueueFixture) as server:
            root = Path(temp)
            credential = root / "rabbitmq-password"
            password = str(uuid4())
            credential.write_text(password + "\n")
            server.auth = "Basic " + b64encode(f"pulse-monitor:{password}".encode()).decode()
            server.requests = []
            server.status = 200
            server.body = json.dumps({
                "messages": 9, "messages_ready": 9, "messages_unacknowledged": 0, "consumers": 0,
                "name": "internal/jobs?", "vhost": "/demo", "node": "synthetic-private-identifier",
            }).encode()
            config_path = root / "agent.ini"
            config_path.write_text(
                AGENT_INI + f"[rabbitmq]\nhost=127.0.0.1\nport={server.server_port}\n"
                "vhost=/demo\nqueue=internal/jobs?\n"
            )
            config = Config.load(config_path)
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                with patch.dict("os.environ", {"CREDENTIALS_DIRECTORY": str(root)}):
                    transport = Mock()
                    transport.send.side_effect = (Outcome.RETRY, Outcome.ACK, Outcome.ACK, Outcome.ACK)
                    self.assertEqual(tick(config, transport), 1)
                    self.assertEqual(tick(config, transport), 0)
                    first, second, replay = [call.args[0] for call in transport.send.call_args_list]
                    self.assertEqual(first, replay)
                    reading = json.loads(first)["values"]["rabbitmq"]
                    self.assertEqual(reading, json.loads(second)["values"]["rabbitmq"])
                    self.assertEqual(reading, {
                        "status": "available", "observedAt": reading["observedAt"],
                        "messages": 9, "messagesReady": 9, "messagesUnacknowledged": 0, "consumers": 0,
                    })
                    self.assertEqual(server.requests, [(
                        "GET", "/api/queues/%2Fdemo/internal%2Fjobs%3F" + COUNTER_QUERY, server.auth,
                    )])

                    cache = config.state_directory / "rabbitmq-queue.json"
                    expired = json.loads(cache.read_text())
                    expired["reading"]["observedAt"] = (
                        datetime.now(timezone.utc) - timedelta(seconds=31)
                    ).isoformat().replace("+00:00", "Z")
                    cache.write_text(json.dumps(expired))
                    server.status = 404
                    self.assertEqual(tick(config, transport), 0)
                    values = json.loads(transport.send.call_args.args[0])["values"]
                    failed = values["rabbitmq"]
                    self.assertEqual(failed, {"status": "unavailable", "observedAt": failed["observedAt"]})
                    self.assertIn("cpuTimesSeconds", values)
                    self.assertEqual(len(server.requests), 2)
            finally:
                server.shutdown()
                thread.join()

    def test_redirect_auth_failure_and_invalid_stats_are_unavailable(self):
        with tempfile.TemporaryDirectory() as temp, HTTPServer(("127.0.0.1", 0), QueueFixture) as server:
            credential = Path(temp) / "rabbitmq-password"
            password = str(uuid4())
            credential.write_text(password)
            server.auth = "Basic " + b64encode(f"pulse-monitor:{password}".encode()).decode()
            server.requests = []
            settings = RabbitMQSettings("127.0.0.1", server.server_port, "/", "jobs", "pulse-monitor", credential)
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                for status, body in (
                    (302, b""), (401, b""), (200, b"malformed"), (200, b"{}"), (200, b"[]"),
                    (200, b" " * (rabbitmq.MAX_RESPONSE_BYTES + 1)),
                    (200, b'{"messages":0,"messages_ready":0,"messages_unacknowledged":0,"consumers":true}'),
                ):
                    with self.subTest(status=status, size=len(body)):
                        server.status, server.body = status, body
                        count = len(server.requests)
                        self.assertEqual(rabbitmq.collect(settings, None)["status"], "unavailable")
                        self.assertEqual(len(server.requests), count + 1)
                        self.assertEqual(server.requests[-1][1], "/api/queues/%2F/jobs" + COUNTER_QUERY)
            finally:
                server.shutdown()
                thread.join()

    def test_network_timeout_keeps_the_probe_unavailable(self):
        with tempfile.TemporaryDirectory() as temp:
            credential = Path(temp) / "rabbitmq-password"
            credential.write_text(str(uuid4()))
            settings = RabbitMQSettings("127.0.0.1", 15672, "/", "jobs", "pulse-monitor", credential)
            with patch.object(rabbitmq.HTTPConnection, "request", side_effect=TimeoutError):
                self.assertEqual(rabbitmq.collect(settings, None)["status"], "unavailable")

    def test_absent_credential_is_unavailable(self):
        settings = RabbitMQSettings("127.0.0.1", 15672, "/", "jobs", "pulse-monitor", Path("/fixture/absent"))
        with patch.object(rabbitmq, "HTTPConnection") as connection:
            self.assertEqual(rabbitmq.collect(settings, None)["status"], "unavailable")
            connection.assert_not_called()

    def test_config_requires_an_approved_queue_and_private_destination(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "agent.ini"
            path.write_text(AGENT_INI + "[rabbitmq]\n")
            self.assertIsNone(Config.load(path).rabbitmq)
            for fragment in (
                "host=192.0.2.1\nqueue=jobs", "host=localhost\nqueue=jobs", "host=127.0.0.1",
                "host=127.0.0.1\nqueue=..", "host=127.0.0.1\nqueue=jobs\nusername=guest",
                "host=127.0.0.1\nqueue=jobs\npassword_file=relative",
            ):
                with self.subTest(fragment=fragment), self.assertRaisesRegex(ValueError, "RabbitMQ"):
                    path.write_text(AGENT_INI + f"[rabbitmq]\n{fragment}\n")
                    Config.load(path)
