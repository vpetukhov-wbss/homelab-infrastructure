"""Aggregate a bounded tail of an approved local NGINX access log."""

import os
import re
import stat
from datetime import datetime, timedelta, timezone
from pathlib import Path

from .cached_probe import observe

MAX_LOG_BYTES = 256 * 1024
WINDOW_SECONDS = 300
RECORD = re.compile(
    rb'[^\[]*\[(?P<stamp>[0-9]{2}/[A-Za-z]{3}/[0-9]{4}:[0-9:]{8} [+-][0-9]{4})\] '
    rb'"[^"]*" (?P<status>[1-5][0-9]{2}) [0-9]+ "[^"]*" "[^"]*"'
    rb'(?: rt=(?:(?P<seconds>[0-9]{1,9})\.(?P<millis>[0-9]{3})|-))?'
)


def collect(path: Path, cache_dir: Path | None) -> dict[str, object]:
    cache = cache_dir / "nginx-access.json" if cache_dir else None
    return observe(cache, [str(path)], lambda: _probe(path), _metrics)


def _metrics(data: dict) -> dict[str, int | bool]:
    requests, errors = data.get("requests"), data.get("serverErrors")
    truncated, p95 = data.get("tailTruncated"), data.get("requestTimeP95Ms")
    if (
        type(requests) is not int or type(errors) is not int or not 0 <= errors <= requests <= MAX_LOG_BYTES
        or type(truncated) is not bool or data.get("windowSeconds") != WINDOW_SECONDS
    ):
        return {}
    result = {"windowSeconds": WINDOW_SECONDS, "requests": requests, "serverErrors": errors, "tailTruncated": truncated}
    if not truncated and requests and type(p95) is int and 0 <= p95 < 10**12:
        result["requestTimeP95Ms"] = p95
    return result


def _tail(path: Path) -> tuple[list[bytes], bool]:
    fd = os.open(path, os.O_RDONLY | os.O_CLOEXEC | os.O_NOFOLLOW | os.O_NONBLOCK)
    with os.fdopen(fd, "rb") as stream:
        info = os.fstat(stream.fileno())
        if not stat.S_ISREG(info.st_mode):
            raise ValueError("Expected a regular access log")
        offset = max(0, info.st_size - MAX_LOG_BYTES)
        # Read one preceding byte to identify whether the first record is complete.
        stream.seek(max(offset - 1, 0))
        tail = stream.read(min(info.st_size, MAX_LOG_BYTES + 1))
    if offset:
        tail = tail[1:] if tail.startswith(b"\n") else tail.partition(b"\n")[2]
    return tail.split(b"\n")[:-1], bool(offset)


def _probe(path: Path) -> dict[str, int | bool]:
    now = datetime.now(timezone.utc)
    cutoff = now - timedelta(seconds=WINDOW_SECONDS)
    records, truncated = _tail(path)
    requests = errors = recognized = 0
    durations = []
    for row in records:
        match = RECORD.fullmatch(row)
        if not match:
            continue
        try:
            stamp = datetime.strptime(match["stamp"].decode("ascii"), "%d/%b/%Y:%H:%M:%S %z")
        except ValueError:
            continue
        recognized += 1
        if not cutoff <= stamp <= now:
            continue
        requests += 1
        errors += match["status"].startswith(b"5")
        if match["seconds"] is not None:
            durations.append(int(match["seconds"]) * 1000 + int(match["millis"]))
    if (truncated or any(records)) and not recognized:
        return {}
    result = {"requests": requests, "serverErrors": errors, "windowSeconds": WINDOW_SECONDS, "tailTruncated": truncated}
    if not truncated and requests and len(durations) == requests:
        durations.sort()
        result["requestTimeP95Ms"] = durations[(95 * requests + 99) // 100 - 1]
    return result
