import stat
from datetime import datetime, timezone
from pathlib import Path

SIDECARS = {"walBytes": "-wal", "sharedMemoryBytes": "-shm", "rollbackJournalBytes": "-journal"}


def collect(paths: dict[str, str]) -> dict[str, dict[str, object]]:
    return {alias: _observe(Path(path)) for alias, path in paths.items()}


def _observe(path: Path) -> dict[str, object]:
    try:
        database = path.resolve(strict=True)
        sizes = {"databaseBytes": _regular_file_size(database)}
        for key, suffix in SIDECARS.items():
            sidecar = database.with_name(database.name + suffix)
            try:
                sizes[key] = _regular_file_size(sidecar)
            except FileNotFoundError:
                sizes[key] = 0
        result = {"status": "available", **sizes, "totalBytes": sum(sizes.values())}
    except (OSError, RuntimeError, ValueError):
        result = {"status": "unavailable"}

    return {
        "observedAt": datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z"),
        **result,
    }


def _regular_file_size(path: Path) -> int:
    info = path.stat()
    if not stat.S_ISREG(info.st_mode):
        raise ValueError("SQLite storage path is not a regular file")
    return info.st_size
