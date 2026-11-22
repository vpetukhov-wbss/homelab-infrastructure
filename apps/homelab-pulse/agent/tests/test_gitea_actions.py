import json
import sqlite3
import tempfile
import unittest
from contextlib import closing
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import Mock

from pulse_agent.collect import tick
from pulse_agent.collectors.gitea_actions import collect
from pulse_agent.config import Config
from pulse_agent.gitea_actions_probe import refresh
from pulse_agent.transport import Outcome


class GiteaActionsTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)
        self.database = self.root / "forge.db"
        self.snapshot = self.root / "actions.json"
        self.ini = self.root / "agent.ini"
        self.ini.write_text(
            "[agent]\ningest_url=http://localhost/internal/v1/telemetry\n"
            f"gitea_actions_snapshot={self.snapshot}\n"
        )

    def test_sanitized_sqlite_summary_is_queued_and_retried(self):
        now = int(datetime.now(timezone.utc).timestamp())
        with closing(sqlite3.connect(self.database)) as connection, connection:
            connection.executescript("""
                CREATE TABLE action_run_job (status, started, stopped, updated);
                CREATE TABLE action_runner (last_online, last_active, deleted, name);
            """)
            connection.executemany("INSERT INTO action_run_job VALUES (?, ?, ?, ?)", [
                (1, now - 70, now - 10, now - 10),
                (2, now - 90, now - 10, now - 10),
                (3, 0, 0, now - 20),
                (4, 0, 0, now - 20),
                (2, now - 4000, now - 3900, now - 3900),
                (5, 0, 0, now - 3900),
                (6, now - 20, 0, now - 20),
                (7, 0, 0, now - 20),
            ])
            connection.executemany("INSERT INTO action_runner VALUES (?, ?, ?, ?)", [
                (now, now, 0, "synthetic-active"),
                (now, now - 30, 0, "synthetic-idle"),
                (now - 80, now - 85, None, "synthetic-offline"),
                (now, now, now, "synthetic-deleted"),
            ])
        before = self.database.read_bytes()
        refresh(self.database, self.snapshot)
        self.assertEqual(self.database.read_bytes(), before)

        transport = Mock()
        transport.send.side_effect = [Outcome.RETRY, Outcome.ACK, Outcome.ACK]
        config = Config.load(self.ini)
        self.assertEqual(tick(config, transport), 1)
        self.assertEqual(tick(config, transport), 0)
        first, _, replay = [call.args[0] for call in transport.send.call_args_list]
        self.assertEqual(first, replay)
        reading = json.loads(first)["values"]["giteaActions"]
        self.assertEqual(reading, {
            "status": "available", "observedAt": reading["observedAt"],
            "waitingJobs": 1, "runningJobs": 1, "blockedJobs": 1,
            "successfulJobsLastHour": 1, "failedJobsLastHour": 1, "cancelledJobsLastHour": 1,
            "skippedJobsLastHour": 1, "activeRunners": 1, "idleRunners": 1, "offlineRunners": 1,
            "timedJobsLastHour": 2, "meanJobDurationSecondsLastHour": 70,
        })
        self.assertNotIn("synthetic-active", self.snapshot.read_text())

    def test_unavailable_and_stale_sources(self):
        self.assertEqual(collect(str(self.snapshot)), {"status": "unavailable"})
        refresh(self.database, self.snapshot)
        self.assertFalse(self.database.exists())
        status = collect(str(self.snapshot))
        self.assertEqual(status, {"status": "unavailable", "observedAt": status["observedAt"]})
        observed = (datetime.now(timezone.utc) - timedelta(minutes=4)).isoformat().replace("+00:00", "Z")
        self.snapshot.write_text(json.dumps({"status": "unavailable", "observedAt": observed}))
        self.assertEqual(collect(str(self.snapshot)), {"status": "stale", "observedAt": observed.split(".")[0] + "Z"})
        self.database.touch()
        refresh(self.database, self.snapshot)
        self.assertEqual(collect(str(self.snapshot))["status"], "unavailable")

    def test_config_requires_an_absolute_snapshot_path(self):
        self.ini.write_text(
            "[agent]\ningest_url=http://localhost/internal/v1/telemetry\ngitea_actions_snapshot=relative.json\n"
        )
        with self.assertRaisesRegex(ValueError, "Gitea Actions snapshot path"):
            Config.load(self.ini)
