import json
from datetime import datetime, timezone
from pathlib import Path

STATES = ("created", "running", "paused", "restarting", "removing", "exited", "dead")
HEALTH = ("starting", "healthy", "unhealthy")
RESTART_POLICIES = ("no", "always", "on-failure", "unless-stopped")
FRESH_SECONDS = 180
MAX_SNAPSHOT_CHARS = 2048
MAX_INTEGER = (1 << 63) - 1


def metrics(data: dict) -> dict[str, object]:
    state, restarts, policy = data.get("state"), data.get("restartCount"), data.get("restartPolicy")
    if state not in STATES or type(restarts) is not int or not 0 <= restarts <= MAX_INTEGER:
        return {}
    if policy not in RESTART_POLICIES:
        return {}
    selected = {"state": state, "restartCount": restarts, "restartPolicy": policy}
    if data.get("health") in HEALTH:
        selected["health"] = data["health"]
    memory = data.get("memoryLimitBytes")
    if type(memory) is int and 0 < memory <= MAX_INTEGER:
        selected["memoryLimitBytes"] = memory
    cores = data.get("cpuLimitCores")
    if type(cores) in (int, float) and 0 < cores <= MAX_INTEGER:
        selected["cpuLimitCores"] = cores
    return selected


def collect(paths: dict[str, str]) -> dict[str, dict[str, object]]:
    now = datetime.now(timezone.utc)
    return {alias: _read_snapshot(Path(path), now) for alias, path in paths.items()}


def _read_snapshot(path: Path, now: datetime) -> dict[str, object]:
    unavailable = {"status": "unavailable"}
    try:
        with path.open(encoding="utf-8") as stream:
            payload = stream.read(MAX_SNAPSHOT_CHARS + 1)
        if len(payload) > MAX_SNAPSHOT_CHARS:
            return unavailable
        snapshot = json.loads(payload)
        observed = snapshot["observedAt"]
        if not isinstance(observed, str) or not observed.endswith("Z"):
            return unavailable
        observed_at = datetime.fromisoformat(observed[:-1] + "+00:00")
        age = (now - observed_at).total_seconds()
        if age < 0:
            return unavailable
        status = snapshot["status"]
        selected = metrics(snapshot) if status == "available" else {}
    except (OSError, KeyError, TypeError, ValueError, RecursionError):
        return unavailable

    stamp = observed_at.isoformat(timespec="seconds").replace("+00:00", "Z")
    if age > FRESH_SECONDS and (selected or status == "unavailable"):
        return {"status": "stale", "observedAt": stamp}
    if selected:
        return {"status": "available", "observedAt": stamp, **selected}
    return {"status": "unavailable", "observedAt": stamp} if status == "unavailable" else unavailable
