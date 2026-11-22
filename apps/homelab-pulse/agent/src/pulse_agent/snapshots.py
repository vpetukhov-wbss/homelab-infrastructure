import json
import os
import tempfile
from datetime import datetime, timezone
from pathlib import Path
from typing import Callable


def write_snapshot(path: Path, snapshot: dict[str, object]) -> None:
    fd, name = tempfile.mkstemp(dir=path.parent, prefix=".snapshot-")
    temporary = Path(name)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            json.dump(snapshot, stream)
            stream.write("\n")
            os.fchmod(stream.fileno(), 0o640)
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def read_snapshot(
    path: Path, now: datetime, extract: Callable[[dict], dict], fresh_seconds: int,
) -> dict[str, object]:
    unavailable = {"status": "unavailable"}
    try:
        with path.open(encoding="utf-8") as stream:
            payload = stream.read(2048 + 1)
        if len(payload) > 2048:
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
        selected = extract(snapshot) if status == "available" else {}
    except (OSError, KeyError, TypeError, ValueError, RecursionError):
        return unavailable

    stamp = observed_at.isoformat(timespec="seconds").replace("+00:00", "Z")
    if age > fresh_seconds and (selected or status == "unavailable"):
        return {"status": "stale", "observedAt": stamp}
    if selected:
        return {"status": "available", "observedAt": stamp, **selected}
    return {"status": "unavailable", "observedAt": stamp} if status == "unavailable" else unavailable
