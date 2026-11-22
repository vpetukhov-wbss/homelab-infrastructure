import sqlite3
import tempfile
import unittest
from contextlib import closing
from pathlib import Path

from pulse_agent.collectors import host, sqlite_storage
from pulse_agent.config import Config


class SQLiteStorageTests(unittest.TestCase):
    def test_host_cycle_reports_live_wal_sizes_under_alias_only(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            database = root / "synthetic-private-name.db"
            alias_path = root / "link.db"
            alias_path.symlink_to(database)
            config_path = root / "agent.ini"
            config_path.write_text(
                "[agent]\ningest_url = http://localhost/internal/v1/telemetry\n"
                f"[sqlite_databases]\nhistory = {alias_path}\nmissing = {root / 'missing.db'}\n"
            )
            config = Config.load(config_path)
            with closing(sqlite3.connect(database)) as connection:
                self.assertEqual(connection.execute("PRAGMA journal_mode=WAL").fetchone(), ("wal",))
                connection.execute("CREATE TABLE fixture (label TEXT)")
                connection.execute("INSERT INTO fixture VALUES ('synthetic-row')")
                connection.commit()

                values = host.collect(config)
                readings = values["sqliteStorage"]
                expected = {
                    "databaseBytes": database.stat().st_size,
                    "walBytes": (root / (database.name + "-wal")).stat().st_size,
                    "sharedMemoryBytes": (root / (database.name + "-shm")).stat().st_size,
                    "rollbackJournalBytes": 0,
                }

        history = readings["history"]
        self.assertEqual(history, {
            "status": "available", "observedAt": history["observedAt"],
            **expected, "totalBytes": sum(expected.values()),
        })
        self.assertGreater(history["walBytes"], 0)
        self.assertEqual(readings["missing"], {
            "status": "unavailable", "observedAt": readings["missing"]["observedAt"],
        })
        self.assertIn("memory", values)
        self.assertNotIn(database.name, str(readings))
        self.assertNotIn("synthetic-row", str(readings))

    def test_rollback_journal_is_observed_until_transaction_ends(self):
        with tempfile.TemporaryDirectory() as temp:
            database = Path(temp) / "rollback.db"
            paths = {"db": str(database)}
            with closing(sqlite3.connect(database)) as connection:
                connection.execute("CREATE TABLE fixture (value INT)")
                connection.execute("INSERT INTO fixture VALUES (1)")
                connection.commit()
                connection.execute("UPDATE fixture SET value = 2")
                pending = sqlite_storage.collect(paths)["db"]
                connection.rollback()
                clean = sqlite_storage.collect(paths)["db"]

        self.assertGreater(pending["rollbackJournalBytes"], 0)
        self.assertEqual(pending["walBytes"], 0)
        self.assertEqual(clean["rollbackJournalBytes"], 0)
        self.assertEqual(clean["totalBytes"], clean["databaseBytes"])

    def test_directories_are_unavailable_and_relative_paths_are_rejected(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            self.assertEqual(sqlite_storage.collect({"db": str(root)})["db"]["status"], "unavailable")
            config_path = root / "agent.ini"
            config_path.write_text(
                "[agent]\ningest_url = http://localhost/internal/v1/telemetry\n"
                "[sqlite_databases]\nhistory = relative.db\n"
            )
            with self.assertRaisesRegex(ValueError, "SQLite database paths must be absolute"):
                Config.load(config_path)
