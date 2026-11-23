import json
import shutil
import ssl
import subprocess
import tempfile
import threading
import unittest
from datetime import datetime, timezone
from pathlib import Path
from socketserver import BaseRequestHandler, TCPServer
from unittest.mock import Mock

from pulse_agent.collect import tick
from pulse_agent.collectors.tls_endpoint import collect
from pulse_agent.config import Config
from pulse_agent.transport import Outcome

INI = "[agent]\ningest_url=http://localhost/internal/v1/telemetry\n"


class TLSConfigTests(unittest.TestCase):
    def test_destination_must_be_private_and_tls_options_are_validated(self):
        with tempfile.TemporaryDirectory() as directory:
            ini = Path(directory) / "agent.ini"
            ini.write_text(INI + "[tls]\nhost=::1\n")
            settings = Config.load(ini).tls
            self.assertEqual((settings.host, settings.port, settings.server_name), ("::1", 443, "::1"))
            for options in (
                "host=edge.example.invalid", "host=192.0.2.2", "host=::1%lo",
                "host=127.0.0.1\nport=0", "host=127.0.0.1\nserver_name=*.example.invalid",
                "host=127.0.0.1\nca_file=demo.pem",
            ):
                with self.subTest(options=options), self.assertRaises(ValueError):
                    ini.write_text(INI + f"[tls]\n{options}\n")
                    Config.load(ini)


class HandshakeHandler(BaseRequestHandler):
    def handle(self):
        try:
            with self.server.context.wrap_socket(self.request, server_side=True):
                pass
        except OSError:
            # Certificate validation tests intentionally close the handshake with an alert.
            pass


@unittest.skipUnless(shutil.which("openssl"), "openssl is required to generate a synthetic TLS fixture")
class TLSCollectorTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        self.certificate, key = self.root / "fixture.pem", self.root / "fixture.key"
        subprocess.run(
            ["openssl", "req", "-x509", "-nodes", "-newkey", "rsa:2048", "-days", "2",
             "-subj", "/CN=edge.example.invalid", "-addext", "subjectAltName=DNS:edge.example.invalid",
             "-keyout", key, "-out", self.certificate],
            capture_output=True, check=True, timeout=10,
        )
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(self.certificate, key)
        self.names = []
        context.set_servername_callback(lambda sock, name, ctx: self.names.append(name))
        server = TCPServer(("127.0.0.1", 0), HandshakeHandler)
        server.context = context
        self.port = server.server_address[1]
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        self.addCleanup(server.server_close)
        self.addCleanup(worker.join)
        self.addCleanup(server.shutdown)
        worker.start()

    def config(self, server_name="edge.example.invalid", trusted=True):
        ini = self.root / "agent.ini"
        ca = self.certificate if trusted else ""
        ini.write_text(
            INI + f"[tls]\nhost=127.0.0.1\nport={self.port}\nserver_name={server_name}\nca_file={ca}\n"
        )
        return Config.load(ini)

    def test_verified_expiry_is_sent_cached_and_retried(self):
        transport = Mock()
        transport.send.side_effect = [Outcome.RETRY, Outcome.ACK, Outcome.ACK]
        config = self.config()
        self.assertEqual(tick(config, transport), 1)
        self.assertEqual(tick(config, transport), 0)
        first, current, replay = [call.args[0] for call in transport.send.call_args_list]
        self.assertEqual(first, replay)
        reading = json.loads(first)["values"]["tls"]
        self.assertEqual(reading, json.loads(current)["values"]["tls"])
        self.assertEqual(set(reading), {"status", "observedAt", "expiresAt"})
        self.assertEqual(reading["status"], "available")
        expiry = datetime.fromisoformat(reading["expiresAt"].replace("Z", "+00:00"))
        self.assertGreater((expiry - datetime.now(timezone.utc)).total_seconds(), 86400)
        self.assertEqual(self.names, ["edge.example.invalid"])

    def test_untrusted_certificate_and_identity_mismatch_are_unavailable(self):
        for config in (self.config(trusted=False), self.config("wrong.example.invalid")):
            with self.subTest(server_name=config.tls.server_name):
                reading = collect(config.tls, None)
                self.assertEqual(reading, {"status": "unavailable", "observedAt": reading["observedAt"]})
