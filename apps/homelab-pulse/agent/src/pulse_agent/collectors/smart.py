import json
from datetime import datetime, timezone
from pathlib import Path
from typing import TextIO

MAX_REPORT_CHARS = 128 * 1024
MAX_SNAPSHOT_CHARS = 2048
FRESH_SECONDS = 180
COUNTERS = (
    "nvmePercentageUsed", "nvmeMediaErrors", "reallocatedSectors", "pendingSectors",
    "offlineUncorrectableSectors",
)
ATA_ATTRIBUTES = {
    "Reallocated_Sector_Ct": "reallocatedSectors",
    "Current_Pending_Sector": "pendingSectors",
    "Offline_Uncorrectable": "offlineUncorrectableSectors",
}


def normalize(source: TextIO) -> dict[str, object]:
    report = _read_json(source, MAX_REPORT_CHARS)
    exit_status = _object(report, "smartctl").get("exit_status")
    seconds = _object(report, "local_time").get("time_t")
    if type(exit_status) is not int or not 0 <= exit_status <= 255 or exit_status & 7:
        return {"status": "unavailable"}
    if type(seconds) is not int or seconds <= 0:
        return {"status": "unavailable"}
    try:
        observed_at = datetime.fromtimestamp(seconds, timezone.utc)
    except (OSError, OverflowError, ValueError):
        return {"status": "unavailable"}

    nvme = _object(report, "nvme_smart_health_information_log")
    temperature = _object(report, "temperature").get("current", nvme.get("temperature"))
    fields = {
        "temperatureCelsius": temperature,
        "healthPassed": _object(report, "smart_status").get("passed"),
        "nvmePercentageUsed": nvme.get("percentage_used"),
        "nvmeMediaErrors": nvme.get("media_errors"),
    }
    table = _object(report, "ata_smart_attributes").get("table")
    if isinstance(table, list):
        for entry in table:
            if isinstance(entry, dict) and isinstance(entry.get("name"), str):
                field = ATA_ATTRIBUTES.get(entry["name"])
                if field:
                    fields[field] = _object(entry, "raw").get("value")
    metrics = _metrics(fields)
    if not metrics:
        return {"status": "unavailable"}
    return {"status": "available", "observedAt": _timestamp(observed_at), **metrics}


def collect(paths: dict[str, str]) -> dict[str, dict[str, object]]:
    now = datetime.now(timezone.utc)
    return {alias: _read_snapshot(Path(path), now) for alias, path in paths.items()}


def _read_snapshot(path: Path, now: datetime) -> dict[str, object]:
    try:
        with path.open(encoding="utf-8") as source:
            snapshot = _read_json(source, MAX_SNAPSHOT_CHARS)
        if snapshot.get("status") != "available":
            return {"status": "unavailable"}
        value = snapshot.get("observedAt")
        if not isinstance(value, str) or not value.endswith("Z"):
            return {"status": "unavailable"}
        observed_at = datetime.fromisoformat(value[:-1] + "+00:00")
        age = (now - observed_at).total_seconds()
    except (OSError, TypeError, ValueError, RecursionError):
        return {"status": "unavailable"}

    metrics = _metrics(snapshot)
    if age < 0 or not metrics:
        return {"status": "unavailable"}
    if age > FRESH_SECONDS:
        return {"status": "stale", "observedAt": _timestamp(observed_at)}
    return {"status": "available", "observedAt": _timestamp(observed_at), **metrics}


def _metrics(fields: dict) -> dict[str, object]:
    metrics: dict[str, object] = {}
    if type(fields.get("temperatureCelsius")) is int:
        metrics["temperatureCelsius"] = fields["temperatureCelsius"]
    if type(fields.get("healthPassed")) is bool:
        metrics["healthPassed"] = fields["healthPassed"]
    for key in COUNTERS:
        value = fields.get(key)
        if type(value) is int and value >= 0:
            metrics[key] = value
    return metrics


def _read_json(source: TextIO, limit: int) -> dict:
    text = source.read(limit + 1)
    if len(text) > limit:
        raise ValueError("SMART JSON exceeds the input limit")
    data = json.loads(text)
    if not isinstance(data, dict):
        raise ValueError("SMART JSON must be an object")
    return data


def _object(data: dict, key: str) -> dict:
    value = data.get(key)
    return value if isinstance(value, dict) else {}


def _timestamp(value: datetime) -> str:
    return value.isoformat(timespec="seconds").replace("+00:00", "Z")
