"""Inspect approved container metadata in the isolated local Docker producer."""

import argparse
import json
import re
import subprocess
from datetime import datetime, timezone
from pathlib import Path

from .collectors import docker
from .docker_stats import read_usage
from .snapshots import write_snapshot

ALIAS = docker.ALIAS
CONTAINER = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,254}")
MAX_CONTAINERS = 8
PROBE_TIMEOUT_SECONDS = 2
DOCKER = "/usr/bin/docker"
FORMAT = (
    '{"name":{{json .Name}},"id":{{json .ID}},"state":{{json .State.Status}},'
    '"health":{{if .State.Health}}{{json .State.Health.Status}}{{else}}null{{end}},'
    '"restartCount":{{json .RestartCount}},"restartPolicy":{{json .HostConfig.RestartPolicy.Name}},'
    '"memoryLimitBytes":{{json .HostConfig.Memory}},"nanoCpus":{{json .HostConfig.NanoCPUs}},'
    '"cpuQuota":{{json .HostConfig.CPUQuota}},"cpuPeriod":{{json .HostConfig.CPUPeriod}}}'
)
VOLUME_FORMAT = FORMAT[:-1] + (
    ',"mounts":[{{$sep := ""}}{{range .Mounts}}{{if eq .Type "volume"}}'
    '{{$sep}}{"name":{{json .Name}},"rw":{{json .RW}}}{{$sep = ","}}{{end}}{{end}}]}'
)
MAX_INSPECT_BYTES = 64 * 1024


def _valid_mapping(mapping: object, limit: int) -> bool:
    return isinstance(mapping, dict) and len(mapping) <= limit and all(
        isinstance(alias, str) and ALIAS.fullmatch(alias)
        and isinstance(name, str) and CONTAINER.fullmatch(name)
        for alias, name in mapping.items()
    )


def refresh(containers: dict[str, str], output_dir: Path, *, volumes: dict[str, str] | None = None) -> None:
    if not _valid_mapping(containers, MAX_CONTAINERS):
        raise ValueError("Invalid Docker container mapping")
    volumes = {} if volumes is None else volumes
    if not _valid_mapping(volumes, docker.MAX_VOLUMES) or len(set(volumes.values())) != len(volumes):
        raise ValueError("Invalid Docker volume mapping")
    aliases = {name: alias for alias, name in volumes.items()}
    for alias, name in containers.items():
        metrics = _probe(name, aliases)
        observed_at = datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")
        write_snapshot(output_dir / f"{alias}.json", {
            "status": "available" if metrics else "unavailable", "observedAt": observed_at, **metrics,
        })


def _probe(name: str, volumes: dict[str, str] | None = None) -> dict[str, object]:
    template = VOLUME_FORMAT if volumes else FORMAT
    limit = MAX_INSPECT_BYTES if volumes else docker.MAX_SNAPSHOT_CHARS
    try:
        result = subprocess.run(
            [DOCKER, "--host=unix:///run/docker.sock", "container", "inspect", "--format", template, "--", name],
            stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            env={"PATH": "/usr/bin:/bin"}, timeout=PROBE_TIMEOUT_SECONDS, check=False,
        )
        if result.returncode != 0 or len(result.stdout) > limit:
            return {}
        data = json.loads(result.stdout)
        if not isinstance(data, dict) or data.get("name") != f"/{name}":
            return {}
        policy = data.get("restartPolicy")
        metrics = docker.metrics({
            "state": data.get("state"), "health": data.get("health"),
            "restartCount": data.get("restartCount"), "restartPolicy": "no" if policy == "" else policy,
            "memoryLimitBytes": data.get("memoryLimitBytes"), "cpuLimitCores": _cpu_limit(data),
        })
        if metrics and volumes and isinstance(data.get("mounts"), list):
            metrics["volumeAttachments"] = _attachments(data["mounts"], volumes)
        if metrics.get("state") == "running":
            metrics["usage"] = read_usage(data.get("id"))
        return metrics
    except (OSError, ValueError, RecursionError, subprocess.TimeoutExpired):
        return {}


def _attachments(mounts: list, volumes: dict[str, str]) -> dict[str, str]:
    modes: dict[str, str] = {}
    for mount in mounts:
        if not isinstance(mount, dict) or not isinstance(mount.get("name"), str):
            continue
        alias, writable = volumes.get(mount["name"]), mount.get("rw")
        if alias is not None and type(writable) is bool:
            mode = "rw" if writable else "ro"
            modes[alias] = mode if modes.get(alias, mode) == mode else "mixed"
    return modes


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
    parser.add_argument("containers", type=Path, help="Protected JSON Docker allowlists")
    parser.add_argument("output_dir", type=Path, help="Snapshot directory")
    args = parser.parse_args()
    try:
        settings = json.loads(args.containers.read_bytes())
        if isinstance(settings, dict) and isinstance(settings.get("containers"), dict):
            if not isinstance(settings.get("volumes"), dict):
                raise ValueError("Invalid Docker volume mapping")
            refresh(settings["containers"], args.output_dir, volumes=settings["volumes"])
        else:
            refresh(settings, args.output_dir)
    except (OSError, ValueError, RecursionError):
        parser.exit(1, "Docker snapshots could not be updated\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
