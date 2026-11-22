"""Read the sanitized Gitea Actions snapshot from the local producer."""

from datetime import datetime, timezone
from pathlib import Path

from ..snapshots import read_snapshot

COUNT_KEYS = (
    "waitingJobs", "runningJobs", "blockedJobs",
    "successfulJobsLastHour", "failedJobsLastHour", "cancelledJobsLastHour", "skippedJobsLastHour",
    "activeRunners", "idleRunners", "offlineRunners", "timedJobsLastHour",
)


def metrics(data: dict) -> dict[str, int]:
    values = {key: data.get(key) for key in COUNT_KEYS}
    if not all(type(value) is int and 0 <= value <= (1 << 63) - 1 for value in values.values()):
        return {}
    mean = data.get("meanJobDurationSecondsLastHour")
    if values["timedJobsLastHour"] > 0:
        if type(mean) is not int or not 0 <= mean <= (1 << 63) - 1:
            return {}
        values["meanJobDurationSecondsLastHour"] = mean
    return values


def collect(path: str) -> dict[str, object]:
    return read_snapshot(Path(path), datetime.now(timezone.utc), metrics, 180)
