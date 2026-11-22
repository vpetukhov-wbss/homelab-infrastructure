"""Run the local SMART producer separately from the networked agent."""

import argparse
import io
import json
import re
import subprocess
from pathlib import Path

from .collectors import smart
from .snapshots import write_snapshot

SMARTCTL = "/usr/sbin/smartctl"
ALIAS = re.compile(r"[a-z][a-z0-9_-]{0,31}")
DEVICE = re.compile(r"sd[a-z]+|nvme[0-9]+n[0-9]+")


def refresh(devices: dict[str, str], output_dir: Path) -> None:
    if not isinstance(devices, dict) or not all(
        isinstance(alias, str) and ALIAS.fullmatch(alias)
        and isinstance(device, str) and DEVICE.fullmatch(device)
        for alias, device in devices.items()
    ):
        raise ValueError("Invalid SMART device mapping")

    for alias, device in devices.items():
        write_snapshot(output_dir / f"{alias}.json", _probe(device))


def _probe(device: str) -> dict[str, object]:
    args = [SMARTCTL, "-j", "-H", "-A"]
    if device.startswith("sd"):
        args += ["-n", "standby"]
    try:
        probe = subprocess.run(
            [*args, f"/dev/{device}"], stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL, encoding="utf-8", errors="replace", timeout=5, check=False,
        )
        if 0 <= probe.returncode <= 255 and not probe.returncode & 7:
            return smart.normalize(io.StringIO(probe.stdout))
    except (OSError, ValueError, RecursionError, subprocess.TimeoutExpired):
        pass
    return {"status": "unavailable"}


def main() -> int:
    parser = argparse.ArgumentParser(description="Update local SMART snapshots")
    parser.add_argument("devices", type=Path, help="Root-owned JSON alias-to-device mapping")
    parser.add_argument("output_dir", type=Path, help="Snapshot directory")
    args = parser.parse_args()
    try:
        refresh(json.loads(args.devices.read_text(encoding="utf-8")), args.output_dir)
    except (OSError, ValueError, RecursionError):
        parser.exit(1, "SMART snapshots could not be updated\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
