import json
import tempfile
import threading
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from socketserver import StreamRequestHandler, TCPServer
from unittest.mock import Mock, patch
from uuid import uuid4

from pulse_agent.collect import tick
from pulse_agent.collectors import redis_info
from pulse_agent.config import Config, RedisSettings
from pulse_agent.transport import Outcome

INFO = (
    b"# Memory\r\nused_memory:4096\r\nmaxmemory:0\r\n"
    b"# Stats\r\ninstantaneous_ops_per_sec:12\r\nkeyspace_hits:57\r\nkeyspace_misses:4\r\n"
    b"evicted_keys:2\r\n"
    b"# Persistence\r\naof_enabled:1\r\naof_current_size:12345\r\n"
    b"# Clients\r\nconnected_clients:3\r\n"
    b"run_id:synthetic-private-value\r\n"
)


class RedisFixture(StreamRequestHandler):
    def handle(self):
        auth = self.command()
        self.server.commands.append(auth)
        if auth != [b"AUTH", b"pulse-monitor", self.server.password]:
            self.wfile.write(b"-WRONGPASS invalid username-password pair\r\n")
            return
        self.wfile.write(b"+OK\r\n")
        self.server.commands.append(self.command())
        self.wfile.write(b"$%d\r\n%s\r\n" % (len(INFO), INFO))

    def command(self):
        fields = []
        for _ in range(int(self.rfile.readline()[1:])):
            length = int(self.rfile.readline()[1:])
            fields.append(self.rfile.read(length + 2)[:-2])
        return fields


class RedisInfoTests(unittest.TestCase):
    def test_authenticated_tick_delivers_allowlisted_metrics_and_caches_the_probe(self):
        with TCPServer(("127.0.0.1", 0), RedisFixture) as server, tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            server.password = str(uuid4()).encode()
            server.commands = []
            credential = root / "redis-password"
            credential.write_bytes(server.password)
            config_path = root / "agent.ini"
            config_path.write_text(
                "[agent]\ningest_url=http://localhost/internal/v1/telemetry\n"
                f"[redis]\nhost=127.0.0.1\nport={server.server_address[1]}\npassword_file={credential}\n"
            )
            config = Config.load(config_path)
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                transport = Mock()
                transport.send.return_value = Outcome.ACK
                self.assertEqual(tick(config, transport), 0)
                self.assertEqual(tick(config, transport), 0)
                first, second = (json.loads(call.args[0])["values"]["redis"]
                                 for call in transport.send.call_args_list)
                self.assertEqual(len(server.commands), 2)
                self.assertEqual(server.commands[1], [b"INFO", b"memory", b"stats", b"persistence", b"clients"])
                self.assertEqual(first, second)
                self.assertEqual(first, {
                    "status": "available", "observedAt": first["observedAt"],
                    "usedMemoryBytes": 4096, "maxMemoryBytes": 0, "operationsPerSecond": 12,
                    "keyspaceHitsTotal": 57, "keyspaceMissesTotal": 4, "evictedKeysTotal": 2,
                    "aofCurrentSizeBytes": 12345, "connectedClients": 3,
                })

                cache = config.state_directory / "redis-info.json"
                expired = json.loads(cache.read_text())
                expired["reading"]["observedAt"] = (
                    datetime.now(timezone.utc) - timedelta(seconds=31)
                ).isoformat().replace("+00:00", "Z")
                cache.write_text(json.dumps(expired))
                credential.write_text(str(uuid4()))
                self.assertEqual(tick(config, transport), 0)
                failed = json.loads(transport.send.call_args.args[0])["values"]["redis"]
                self.assertEqual(failed, {"status": "unavailable", "observedAt": failed["observedAt"]})
                self.assertEqual(len(server.commands), 3)
            finally:
                server.shutdown()
                thread.join()

    def test_unavailable_credential_and_socket_failure_do_not_raise(self):
        settings = RedisSettings("127.0.0.1", 6379, "pulse-monitor", Path("/fixture/missing-redis-credential"))
        self.assertEqual(redis_info.collect(settings, None)["status"], "unavailable")
        for error in (TimeoutError(), ValueError()):
            with patch.object(redis_info, "_probe", side_effect=error):
                self.assertEqual(redis_info.collect(settings, None)["status"], "unavailable")

    def test_metrics_omit_disabled_aof_and_invalid_fields(self):
        self.assertEqual(redis_info._metrics(
            b"used_memory:0\r\naof_enabled:0\r\naof_current_size:2048\r\nkeyspace_hits:-1\r\n"
        ), {"usedMemoryBytes": 0})

    def test_redis_requires_an_explicit_private_endpoint_and_dedicated_account(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "agent.ini"
            for fragment in ("host=192.0.2.1", "host=localhost", "host=127.0.0.1\nusername=default"):
                with self.subTest(fragment=fragment), self.assertRaisesRegex(ValueError, "Redis requires"):
                    path.write_text("[agent]\ningest_url=http://localhost/internal/v1/telemetry\n"
                                    f"[redis]\n{fragment}\n")
                    Config.load(path)
