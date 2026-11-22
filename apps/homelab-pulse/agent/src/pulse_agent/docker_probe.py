"""Inspect approved container metadata in the isolated local Docker producer."""

import argparse
import json
import re
import subprocess
from datetime import datetime, timezone
from pathlib import Path

from .collectors import docker
from .snapshots import write_snapshot

ALIAS = re.compile(r"[a-z][a-z0-9_-]{0,31}")
CONTAINER = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,254}")
MAX_CONTAINERS = 8
PROBE_TIMEOUT_SECONDS = 2
DOCKER = "/usr/bin/docker"
FORMAT = (
    '{"name":{{json .Name}},"state":{{json .State.Status}},'
    '"health":{{if .State.Health}}{{json .State.Health.Status}}{{else}}null{{end}},'
    '"restartCount":{{json .RestartCount}},"restartPolicy":{{json .HostConfig.RestartPolicy.Name}},'
    '"memoryLimitBytes":{{json .HostConfig.Memory}},"nanoCpus":{{json .HostConfig.NanoCPUs}},'
    '"cpuQuota":{{json .HostConfig.CPUQuota}},"cpuPeriod":{{json .HostConfig.CPUPeriod}}}'
)


def refresh(containers: dict[str, str], output_dir: Path) -> None:
    if not isinstance(containers, dict) or len(containers) > MAX_CONTAINERS or not all(
        isinstance(alias, str) and ALIAS.fullmatch(alias)
        and isinstance(name, str) and CONTAINER.fullmatch(name)
        for alias, name in containers.items()
    ):
        raise ValueError("Invalid Docker container mapping")
    for alias, name in containers.items():
        metrics = _probe(name)
        observed_at = datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")
        write_snapshot(output_dir / f"{alias}.json", {
            "status": "available" if metrics else "unavailable", "observedAt": observed_at, **metrics,
        })


def _probe(name: str) -> dict[str, object]:
    try:
        result = subprocess.run(
            [DOCKER, "--host=unix:///run/docker.sock", "container", "inspect", "--format", FORMAT, "--", name],
            stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            env={"PATH": "/usr/bin:/bin"}, timeout=PROBE_TIMEOUT_SECONDS, check=False,
        )
        if result.returncode != 0 or len(result.stdout) > docker.MAX_SNAPSHOT_CHARS:
            return {}
        data = json.loads(result.stdout)
        if not isinstance(data, dict) or data.get("name") != f"/{name}":
            return {}
        policy = data.get("restartPolicy")
        return docker.metrics({
            "state": data.get("state"), "health": data.get("health"),
            "restartCount": data.get("restartCount"), "restartPolicy": "no" if policy == "" else policy,
            "memoryLimitBytes": data.get("memoryLimitBytes"), "cpuLimitCores": _cpu_limit(data),
        })
    except (OSError, ValueError, RecursionError, subprocess.TimeoutExpired):
        return {}


def _cpu_limit(data: dict) -> float | None:
    nano = data.get("nanoCpus")
    if type(nano) is int and 0 < nano <= docker.MAX_INTEGER:
        return nano / 1_000_000_000
    quota, period = data.get("cpuQuota"), data.get("cpuPeriod")
    if (
        type(quota) is int and type(period) is int
        and 0 < quota <= docker.MAX_INTEGER and 0 <= period <= docker.MAX_INTEGER
    ):
        return quota / (period or 100_000)
    return None


def main() -> int:
    parser = argparse.ArgumentParser(description="Update local Docker workload snapshots")
    parser.add_argument("containers", type=Path, help="Protected JSON alias-to-container mapping")
    parser.add_argument("output_dir", type=Path, help="Snapshot directory")
    args = parser.parse_args()
    try:
        refresh(json.loads(args.containers.read_bytes()), args.output_dir)
    except (OSError, ValueError, RecursionError):
        parser.exit(1, "Docker snapshots could not be updated\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
