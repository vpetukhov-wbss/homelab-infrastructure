"""Measure operator-approved local Docker volume directories without Docker access."""

import argparse
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path

from .collectors.docker import ALIAS, volume_metrics
from .snapshots import write_snapshot

MAX_PATHS = 8


def _local_path(value: object) -> bool:
    if not isinstance(value, str) or not value.isprintable() or len(value) > 4096:
        return False
    path = Path(value)
    return path.is_absolute() and len(path.parts) > 1 and str(path) == value and ".." not in path.parts


def _run(argv: list[str], timeout: int) -> bytes:
    try:
        result = subprocess.run(
            argv, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            env={"PATH": "/usr/bin:/bin", "LC_ALL": "C"}, timeout=timeout, check=False,
        )
        if result.returncode == 0 and len(result.stdout) <= 8192:
            return result.stdout
    except (OSError, subprocess.TimeoutExpired):
        pass
    return b""


def _probe(path: str) -> dict[str, int]:
    folder = Path(path)
    try:
        if folder.is_symlink() or not folder.is_dir():
            return {}
    except OSError:
        return {}
    fields = _run(["/usr/bin/stat", "-f", "--format=%S %b %a", "--", path], 1).split()
    values = {}
    if len(fields) == 3 and all(field.isdigit() and len(field) <= 20 for field in fields):
        unit, total, free = map(int, fields)
        if unit > 0:
            values.update(filesystemCapacityBytes=unit * total, filesystemAvailableBytes=unit * free)
    size = _run(["/usr/bin/du", "-x", "-s", "-B1", "--", path], 5).partition(b"\t")[0]
    if size.isdigit() and len(size) <= 20:
        values["usedBytes"] = int(size)
    return volume_metrics(values)


def refresh(paths: dict[str, str], output_dir: Path) -> None:
    if not isinstance(paths, dict) or len(paths) > MAX_PATHS or not all(
        isinstance(alias, str) and ALIAS.fullmatch(alias) and _local_path(path)
        for alias, path in paths.items()
    ):
        raise ValueError("Invalid Docker volume paths")
    for alias, path in paths.items():
        values = _probe(path)
        stamp = datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")
        write_snapshot(output_dir / f"{alias}.json", {
            "status": "available" if values else "unavailable", "observedAt": stamp, **values,
        })


def main() -> int:
    parser = argparse.ArgumentParser(description="Measure local Docker volume storage")
    parser.add_argument("paths", type=Path)
    parser.add_argument("output_dir", type=Path)
    args = parser.parse_args()
    try:
        refresh(json.loads(args.paths.read_bytes()), args.output_dir)
    except (OSError, ValueError, RecursionError):
        parser.exit(1, "Docker volume snapshots could not be updated\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
