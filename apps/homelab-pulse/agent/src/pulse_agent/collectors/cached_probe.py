import json
import os
import tempfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Callable

POLL_SECONDS = 30


def observe(
    cache: Path | None, scope: list[str | int], probe: Callable[[], dict],
    filter_metrics: Callable[[dict], dict],
) -> dict[str, object]:
    if cache:
        reading = _cached_reading(cache, scope, filter_metrics)
        if reading:
            return reading
    try:
        metrics = probe()
    except (OSError, ValueError):
        metrics = {}
    reading = {
        "status": "available" if metrics else "unavailable",
        "observedAt": datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z"),
        **metrics,
    }
    if cache:
        try:
            _save(cache, {"scope": scope, "reading": reading})
        except OSError:
            pass
    return reading


def _cached_reading(
    path: Path, scope: list[str | int], filter_metrics: Callable[[dict], dict],
) -> dict[str, object] | None:
    try:
        with path.open(encoding="utf-8") as stream:
            data = json.loads(stream.read(4096))
        if data["scope"] != scope:
            return None
        reading = data["reading"]
        timestamp = reading["observedAt"]
        if not timestamp.endswith("Z"):
            return None
        observed_at = datetime.fromisoformat(timestamp[:-1] + "+00:00")
        if not 0 <= (datetime.now(timezone.utc) - observed_at).total_seconds() < POLL_SECONDS:
            return None
        state = {"observedAt": timestamp, "status": reading["status"]}
        if state["status"] == "unavailable":
            return state
        metrics = filter_metrics(reading)
        if state["status"] == "available" and metrics:
            return {**state, **metrics}
    except (OSError, AttributeError, KeyError, TypeError, ValueError, RecursionError):
        pass
    return None


def _save(path: Path, data: dict) -> None:
    fd, name = tempfile.mkstemp(dir=path.parent, prefix=f".{path.stem}-")
    temporary = Path(name)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            json.dump(data, stream)
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)
