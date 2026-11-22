import json
import tempfile
import threading
import unittest
from datetime import datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from unittest.mock import patch
from uuid import uuid4

from pulse_agent.collect import tick
from pulse_agent.collectors import nginx
from pulse_agent.config import Config
from pulse_agent.transport import Transport

STUB_STATUS_BODY = (
    b"Active connections: 8\n"
    b"server accepts handled requests\n"
    b" 24 23 51\n"
    b"Reading: 1 Writing: 2 Waiting: 5\n"
)


class FixtureServer(BaseHTTPRequestHandler):
    def do_GET(self):
        self.server.get_requests.append((self.path, self.headers.get("Authorization")))
        if self.path == "/redirect":
            self.send_response(302)
            self.send_header("Location", "/stub_status")
            self.end_headers()
            return
        bodies = {"/stub_status": STUB_STATUS_BODY, "/malformed": b"service unavailable",
                  "/oversized": STUB_STATUS_BODY + b" " * 1024}
        self.send_response(200 if self.path in bodies else 503)
        self.end_headers()
        self.wfile.write(bodies.get(self.path, b""))

    def do_POST(self):
        body = self.rfile.read(int(self.headers["Content-Length"]))
        self.server.received.append(json.loads(body))
        self.send_response(204)
        self.end_headers()

    def log_message(self, *_):
        pass


class NginxTests(unittest.TestCase):
    def test_nginx_probe_delivers_counters_and_isolates_failed_probes(self):
        with ThreadingHTTPServer(("127.0.0.1", 0), FixtureServer) as server, tempfile.TemporaryDirectory() as temp:
            server.get_requests, server.received = [], []
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                base = f"http://127.0.0.1:{server.server_port}"
                ingest = f"{base}/internal/v1/telemetry"
                transport = Transport(ingest, str(uuid4()), 1)
                config_path = Path(temp) / "agent.ini"
                for route in ("/stub_status", "/not_ready", "/redirect", "/malformed", "/oversized"):
                    config_path.write_text(f"[agent]\ningest_url={ingest}\nnginx_status_url={base}{route}\n")
                    with patch.dict("os.environ", {"no_proxy": "", "http_proxy": "http://127.0.0.1:1"}):
                        self.assertEqual(tick(Config.load(config_path), transport), 0)
            finally:
                server.shutdown()
                thread.join()

        self.assertEqual(server.get_requests, [(route, None) for route in
                         ("/stub_status", "/not_ready", "/redirect", "/malformed", "/oversized")])
        self.assertEqual(len(server.received), 5)
        values = [payload["values"] for payload in server.received]
        self.assertTrue(all("memory" in payload for payload in values))
        first = values[0]["nginx"]
        self.assertEqual(first, {
            "status": "available", "observedAt": first["observedAt"],
            "activeConnections": 8, "acceptedConnectionsTotal": 24, "handledConnectionsTotal": 23,
            "requestsTotal": 51, "readingConnections": 1, "writingConnections": 2, "waitingConnections": 5,
        })
        for payload in values[1:]:
            self.assertEqual(payload["nginx"], {
                "status": "unavailable", "observedAt": payload["nginx"]["observedAt"],
            })
        for payload in values:
            timestamp = payload["nginx"]["observedAt"]
            self.assertTrue(timestamp.endswith("Z"))
            datetime.fromisoformat(timestamp[:-1] + "+00:00")

    def test_nginx_timeout_marks_the_probe_unavailable(self):
        with patch("pulse_agent.collectors.nginx.build_opener") as build_opener:
            open_url = build_opener.return_value.open
            open_url.side_effect = TimeoutError
            probe = nginx.collect("http://127.0.0.1/stub_status")
            self.assertEqual(open_url.call_args.kwargs["timeout"], 1)
        self.assertEqual(probe["status"], "unavailable")

    def test_nginx_endpoint_requires_private_literal_without_url_credentials(self):
        prefix = "[agent]\ningest_url=http://localhost/internal/v1/telemetry\nnginx_status_url="
        with tempfile.TemporaryDirectory() as temp:
            config = Path(temp) / "agent.ini"
            for url in ("http://example.invalid/stub_status", "http://192.0.2.1/stub_status",
                        "http://user@127.0.0.1/stub_status", "http://127.0.0.1/stub_status?"):
                with self.subTest(url=url), self.assertRaisesRegex(ValueError, "NGINX status URL"):
                    config.write_text(prefix + url + "\n")
                    Config.load(config)
            for url in ("http://127.0.0.1/stub_status", "https://[::1]/stub_status", ""):
                with self.subTest(url=url):
                    config.write_text(prefix + url + "\n")
                    self.assertEqual(Config.load(config).nginx_status_url, url or None)
