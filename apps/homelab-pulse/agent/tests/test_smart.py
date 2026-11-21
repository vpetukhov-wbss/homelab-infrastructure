import json
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from io import StringIO
from pathlib import Path

from pulse_agent.collectors import smart
from pulse_agent.config import Config


class SmartTests(unittest.TestCase):
    def test_nvme_failure_report_keeps_health_and_drops_metadata(self):
        report = {
            "smartctl": {"exit_status": 8, "argv": ["smartctl", "fixture-device"]},
            "serial_number": "fixture-serial",
            "local_time": {"time_t": 1_700_000_000},
            "smart_status": {"passed": False},
            "nvme_smart_health_information_log": {
                "temperature": 45, "percentage_used": 120, "media_errors": 3,
            },
        }

        snapshot = smart.normalize(StringIO(json.dumps(report)))

        self.assertEqual(snapshot, {
            "status": "available", "observedAt": "2023-11-14T22:13:20Z",
            "healthPassed": False, "temperatureCelsius": 45, "nvmePercentageUsed": 120,
            "nvmeMediaErrors": 3,
        })
        report["smartctl"]["exit_status"] = 2
        self.assertEqual(smart.normalize(StringIO(json.dumps(report))), {"status": "unavailable"})

    def test_ata_report_keeps_only_supported_typed_counters(self):
        report = {
            "smartctl": {"exit_status": 0},
            "local_time": {"time_t": 1_700_000_000},
            "ata_smart_attributes": {"table": [
                {"name": "Reallocated_Sector_Ct", "raw": {"value": 0, "string": "fixture"}},
                {"name": "Current_Pending_Sector", "raw": {"value": True}},
                {"name": "Offline_Uncorrectable", "raw": {"value": 2}},
                {"name": "Temperature_Celsius", "raw": {"value": 38}},
                {"name": "Unknown_Attribute", "raw": {"value": 48}},
            ]},
            "temperature": {"current": 38},
        }
        self.assertEqual(smart.normalize(StringIO(json.dumps(report))), {
            "status": "available", "observedAt": "2023-11-14T22:13:20Z",
            "reallocatedSectors": 0, "offlineUncorrectableSectors": 2, "temperatureCelsius": 38,
        })

    def test_snapshot_reader_handles_age_unavailable_sources_and_extra_data(self):
        now = datetime.now(timezone.utc).replace(microsecond=0)
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp)
            cases = {"fresh": now, "old": now - timedelta(minutes=4), "future": now + timedelta(minutes=1)}
            for name, timestamp in cases.items():
                (path / name).write_text(json.dumps({
                    "status": "available",
                    "observedAt": timestamp.isoformat().replace("+00:00", "Z"),
                    "temperatureCelsius": 35, "serialNumber": "fixture-serial",
                }))
            (path / "invalid").write_text("{")
            config_path = path / "agent.ini"
            aliases = (*cases, "invalid", "missing")
            config_path.write_text(
                "[agent]\ningest_url = http://localhost/internal/v1/telemetry\n"
                "[smart_snapshots]\n" + "".join(f"{alias} = {path / alias}\n" for alias in aliases)
            )

            readings = smart.collect(Config.load(config_path).smart_snapshots)

        self.assertEqual(readings, {
            "fresh": {"status": "available", "observedAt": now.isoformat().replace("+00:00", "Z"),
                      "temperatureCelsius": 35},
            "old": {"status": "stale", "observedAt": cases["old"].isoformat().replace("+00:00", "Z")},
            "future": {"status": "unavailable"},
            "invalid": {"status": "unavailable"},
            "missing": {"status": "unavailable"},
        })
