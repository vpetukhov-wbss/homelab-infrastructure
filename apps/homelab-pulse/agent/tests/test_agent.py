import io
import json
import logging
import os
import tempfile
import threading
import unittest
from contextlib import redirect_stdout
from functools import partial
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from unittest.mock import patch
from uuid import uuid4

from pulse_agent.cli import main
from pulse_agent.collect import tick
from pulse_agent.collectors import host, inventory
from pulse_agent.config import Config, read_token
from pulse_agent.models import Sample
from pulse_agent.outbox import Outbox
from pulse_agent.transport import Outcome, Transport

OFFLINE_TOKEN = str(uuid4())


class AgentTests(unittest.TestCase):
    def test_hardware_inventory_exports_only_reviewable_fields(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp)
            (path / "cpuinfo").write_text(
                "processor: 0\nmodel name: Fixture CPU\nphysical id: 0\ncore id: 0\n\n"
                "processor: 1\nphysical id: 0\ncore id: 0\n\n"
                "processor: 2\nphysical id: 1\ncore id: 0\n\n"
                "processor: 3\nphysical id: 1\ncore id: 0\n"
            )
            (path / "meminfo").write_text("MemTotal: 8192 kB\n")
            (path / "sys_vendor").write_text("Fixture Vendor\n")
            (path / "product_name").write_text("Fixture Desktop\n")
            (path / "product_serial").write_text("synthetic-serial\n")

            with patch("pulse_agent.collectors.inventory.platform.machine", return_value="x86_64"):
                profile = inventory.collect(proc=path, dmi=path)

        self.assertEqual(profile, {
            "machine": {"manufacturer": "Fixture Vendor", "model": "Fixture Desktop"},
            "cpu": {"architecture": "x86_64", "model": "Fixture CPU", "logicalCpuCount": 4,
                    "physicalCoreCount": 2},
            "memory": {"osVisibleBytes": 8 * 1024 * 1024},
        })

    def test_hardware_inventory_omits_unavailable_vm_details(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp)
            (path / "cpuinfo").write_text("processor: 0\n\nprocessor: 1\n")
            (path / "meminfo").write_text("MemTotal: 8192 kB\n")
            profile = inventory.collect(proc=path, dmi=path / "missing")

        self.assertEqual(profile["machine"], {})
        self.assertEqual(profile["cpu"]["logicalCpuCount"], 2)
        self.assertNotIn("physicalCoreCount", profile["cpu"])
        self.assertNotIn("model", profile["cpu"])

    def test_inventory_cli_reports_aliased_disks_without_local_identifiers(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp)
            (path / "cpuinfo").write_text("processor: 0\n")
            (path / "meminfo").write_text("MemTotal: 8192 kB\n")
            block_root = path / "block"
            disk = block_root / "nvme0n1"
            (disk / "device").mkdir(parents=True)
            (disk / "queue").mkdir()
            (disk / "size").write_text("1000\n")
            (disk / "device/model").write_text("  Fixture SSD \n")
            (disk / "device/serial").write_text("synthetic-serial\n")
            (disk / "queue/logical_block_size").write_text("4096\n")
            (disk / "queue/rotational").write_text("0\n")
            (block_root / "loop0").mkdir()
            (block_root / "loop0/size").write_text("1000\n")
            config = path / "agent.ini"
            config.write_text(
                "[agent]\ningest_url = http://localhost/internal/v1/telemetry\n"
                "[disks]\nsystem = nvme0n1\nmissing = sdb\nvirtual = loop0\n"
            )
            collect = partial(inventory.collect, proc=path, dmi=path / "dmi", block_root=block_root)
            output = io.StringIO()
            with patch("pulse_agent.cli.inventory.collect", side_effect=collect), redirect_stdout(output):
                self.assertEqual(main(["--config", str(config), "inventory"]), 0)

        self.assertEqual(json.loads(output.getvalue())["disks"], {
            "system": {
                "status": "available", "capacityBytes": 512_000, "model": "Fixture SSD", "rotational": False,
            },
            "missing": {"status": "unavailable"},
            "virtual": {"status": "unavailable"},
        })
        self.assertNotIn("nvme0n1", output.getvalue())
        self.assertNotIn("synthetic-serial", output.getvalue())

    def test_config_rejects_paths_as_disk_names(self):
        with tempfile.TemporaryDirectory() as temp:
            config = Path(temp) / "agent.ini"
            config.write_text(
                "[agent]\ningest_url = http://localhost/internal/v1/telemetry\n"
                "[disks]\nsystem = ../sda\n"
            )
            with self.assertRaisesRegex(ValueError, "Invalid block device name"):
                Config.load(config)

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
