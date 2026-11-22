import json
import os
import tempfile
import unittest
from pathlib import Path
from subprocess import CompletedProcess, TimeoutExpired
from unittest.mock import Mock, patch

from pulse_agent.collect import tick
from pulse_agent.collectors import postgresql
from pulse_agent.config import Config, PostgreSQLSettings
from pulse_agent.transport import Outcome

METRICS = {
    "databaseBytes": 8192,
    "connectedBackends": 2,
    "commitsTotal": 10,
    "rollbacksTotal": 0,
    "bufferHitsTotal": 48,
    "blocksReadTotal": 1,
    "deadlocksTotal": 0,
    "inRecovery": False,
    "statsResetAt": "2026-01-01T00:00:00.123456Z",
}


class PostgreSQLTests(unittest.TestCase):
    def test_tick_delivers_allowlisted_statistics_and_reuses_the_observation(self):
        report = METRICS | {"databaseName": "synthetic-private-database"}
        fixture = CompletedProcess([], 0, stdout=json.dumps(report).encode())
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            credential = root / "postgres-pgpass"
            credential.touch(0o600)
            source = root / "agent.ini"
            source.write_text(
                "[agent]\ningest_url=http://localhost/internal/v1/telemetry\n"
                "[postgresql]\nhost=127.0.0.1\ndatabase=synthetic-private-database\n"
            )
            config = Config.load(source)
            transport = Mock()
            transport.send.return_value = Outcome.ACK

            with (
                patch.dict(os.environ, {"CREDENTIALS_DIRECTORY": temp, "PGSERVICE": "fixture-service"}),
                patch("pulse_agent.collectors.postgresql.subprocess.run", return_value=fixture) as run,
            ):
                self.assertEqual(tick(config, transport), 0)
                self.assertEqual(tick(config, transport), 0)

        run.assert_called_once()
        self.assertEqual(run.call_args.kwargs["timeout"], 2)
        env = run.call_args.kwargs["env"]
        self.assertEqual(env["PGPASSFILE"], str(credential))
        self.assertIn("default_transaction_read_only=on", env["PGOPTIONS"])
        self.assertNotIn("PGSERVICE", env)
        self.assertNotIn("PGPASSWORD", env)
        args = run.call_args.args[0]
        self.assertIn("-w", args)
        self.assertNotIn("synthetic-private-database", args[-1])
        first, second = [json.loads(call.args[0])["values"]["postgresql"] for call in transport.send.call_args_list]
        self.assertEqual(first, second)
        self.assertEqual(first, {"status": "available", "observedAt": first["observedAt"], **METRICS})

    def test_missing_credential_timeout_and_bad_result_are_unavailable(self):
        with tempfile.TemporaryDirectory() as temp:
            credential = Path(temp) / "postgres-pgpass"
            settings = PostgreSQLSettings("127.0.0.1", 5432, "example", "pulse-monitor", credential)
            self.assertEqual(postgresql.collect(settings, None)["status"], "unavailable")
            credential.touch(0o600)
            with patch("pulse_agent.collectors.postgresql.subprocess.run", side_effect=TimeoutExpired("psql", 2)):
                self.assertEqual(postgresql.collect(settings, None)["status"], "unavailable")
            for result in (CompletedProcess([], 1, stdout=b""), CompletedProcess([], 0, stdout=b"invalid")):
                with patch("pulse_agent.collectors.postgresql.subprocess.run", return_value=result):
                    self.assertEqual(postgresql.collect(settings, None)["status"], "unavailable")

    def test_statistics_filter_ignores_missing_reset_and_rejects_bad_counters(self):
        without_reset = METRICS | {"statsResetAt": None}
        self.assertEqual(postgresql._metrics(without_reset), {k: v for k, v in METRICS.items() if k != "statsResetAt"})
        self.assertEqual(postgresql._metrics(METRICS | {"commitsTotal": True}), {})
        self.assertEqual(postgresql._metrics(METRICS | {"connectedBackends": -1}), {})

    def test_connection_settings_disallow_external_hosts_superuser_and_dsn(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "agent.ini"
            suffixes = ("host=192.0.2.1", "host=127.0.0.1\nusername=postgres",
                        "host=127.0.0.1\ndatabase=dbname=other")
            for suffix in suffixes:
                with self.subTest(suffix=suffix), self.assertRaisesRegex(ValueError, "PostgreSQL requires"):
                    path.write_text("[agent]\ningest_url=http://localhost/internal/v1/telemetry\n"
                                    f"[postgresql]\n{suffix}\n")
                    Config.load(path)
