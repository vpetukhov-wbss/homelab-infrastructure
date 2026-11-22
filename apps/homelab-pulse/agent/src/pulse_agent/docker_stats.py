"""Read a bounded, non-streaming Docker Engine stats response."""

import json
import re
import subprocess

from .collectors.docker import MAX_INTEGER, usage_metrics

CONTAINER_ID = re.compile(r"[a-f0-9]{64}")
MAX_RESPONSE_BYTES = 64 * 1024


def read_usage(container_id: object) -> dict[str, object]:
    if not isinstance(container_id, str) or not CONTAINER_ID.fullmatch(container_id):
        return {"status": "unavailable"}
    try:
        result = subprocess.run(
            [
                "/usr/bin/curl", "--disable", "--silent", "--fail", "--noproxy", "*",
                "--unix-socket", "/run/docker.sock", "--max-time", "3",
                "--max-filesize", str(MAX_RESPONSE_BYTES), "--write-out", "\n%{http_code}", "--",
                f"http://localhost/containers/{container_id}/stats?stream=false",
            ],
            stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            env={"PATH": "/usr/bin:/bin"}, timeout=4, check=False,
        )
        body, _, status = result.stdout.rpartition(b"\n")
        if result.returncode == 0 and status == b"200" and len(body) <= MAX_RESPONSE_BYTES:
            report = json.loads(body)
            if isinstance(report, dict):
                values = usage_metrics({
                    "cpuUsageCores": _cpu_usage(report),
                    "memoryUsageBytes": _memory_usage(report),
                })
                if values:
                    return {"status": "available", **values}
    except (OSError, ValueError, RecursionError, subprocess.TimeoutExpired):
        pass
    return {"status": "unavailable"}


def _object(data: dict, key: str) -> dict:
    value = data.get(key)
    return value if isinstance(value, dict) else {}


def _cpu_usage(report: dict) -> float | None:
    current = _object(report, "cpu_stats")
    previous = _object(report, "precpu_stats")
    usage = _object(current, "cpu_usage")
    cores = current.get("online_cpus")
    per_cpu = usage.get("percpu_usage")
    if (cores is None or cores == 0) and isinstance(per_cpu, list):
        cores = len(per_cpu)
    total = usage.get("total_usage")
    before = _object(previous, "cpu_usage").get("total_usage")
    system, system_before = current.get("system_cpu_usage"), previous.get("system_cpu_usage")
    counters = (cores, total, before, system, system_before)
    if not all(type(value) is int and 0 <= value <= MAX_INTEGER for value in counters):
        return None
    if cores > 0 and system > system_before > 0 and total >= before:
        return (total - before) / (system - system_before) * cores
    return None


def _memory_usage(report: dict) -> int | None:
    memory = _object(report, "memory_stats")
    used = memory.get("usage")
    if type(used) is not int or not 0 <= used <= MAX_INTEGER:
        return None
    cache = _object(memory, "stats")
    inactive = cache.get("total_inactive_file", cache.get("inactive_file"))
    if type(inactive) is int and 0 <= inactive <= used:
        return used - inactive
    return used
