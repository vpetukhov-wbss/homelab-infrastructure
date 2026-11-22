import re
from datetime import datetime, timezone
from pathlib import Path

from ..snapshots import read_snapshot

STATES = ("created", "running", "paused", "restarting", "removing", "exited", "dead")
HEALTH = ("starting", "healthy", "unhealthy")
RESTART_POLICIES = ("no", "always", "on-failure", "unless-stopped")
FRESH_SECONDS = 180
MAX_SNAPSHOT_CHARS = 2048
MAX_INTEGER = (1 << 63) - 1
MAX_VOLUMES = 16
ALIAS = re.compile(r"[a-z][a-z0-9_-]{0,31}")


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
    attachments = data.get("volumeAttachments")
    if isinstance(attachments, dict) and len(attachments) <= MAX_VOLUMES:
        selected["volumeAttachments"] = {
            alias: mode for alias, mode in attachments.items()
            if ALIAS.fullmatch(alias) and mode in ("ro", "rw", "mixed")
        }
    usage = data.get("usage")
    if isinstance(usage, dict) and usage.get("status") in ("available", "unavailable"):
        values = usage_metrics(usage) if usage["status"] == "available" else {}
        selected["usage"] = {"status": "available" if values else "unavailable", **values}
    return selected


def usage_metrics(data: dict) -> dict[str, object]:
    values = {}
    cpu = data.get("cpuUsageCores")
    if type(cpu) in (int, float) and 0 <= cpu <= MAX_INTEGER:
        values["cpuUsageCores"] = cpu
    memory = data.get("memoryUsageBytes")
    if type(memory) is int and 0 <= memory <= MAX_INTEGER:
        values["memoryUsageBytes"] = memory
    return values


def collect(paths: dict[str, str]) -> dict[str, dict[str, object]]:
    now = datetime.now(timezone.utc)
    return {alias: read_snapshot(Path(path), now, metrics, FRESH_SECONDS) for alias, path in paths.items()}


def collect_volumes(paths: dict[str, str]) -> dict[str, dict[str, object]]:
    now = datetime.now(timezone.utc)
    return {alias: read_snapshot(Path(path), now, volume_metrics, 900) for alias, path in paths.items()}


def volume_metrics(data: dict) -> dict[str, int]:
    values = {}
    used = data.get("usedBytes")
    if type(used) is int and 0 <= used <= MAX_INTEGER:
        values["usedBytes"] = used
    capacity, free = data.get("filesystemCapacityBytes"), data.get("filesystemAvailableBytes")
    if type(capacity) is int and type(free) is int and 0 <= free <= capacity <= MAX_INTEGER:
        values.update(filesystemCapacityBytes=capacity, filesystemAvailableBytes=free)
    return values
