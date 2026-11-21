import logging
import os
from pathlib import Path

from ..config import Config

LOG = logging.getLogger(__name__)
CPU_NAMES = ("user", "nice", "system", "idle", "iowait", "irq", "softirq", "steal")
MEMORY_NAMES = {
    "MemTotal": "totalBytes",
    "MemAvailable": "availableBytes",
    "SwapTotal": "swapTotalBytes",
    "SwapFree": "swapFreeBytes",
}


def collect(
    config: Config, proc: Path = Path("/proc"), network_root: Path = Path("/sys/class/net"),
) -> dict[str, object]:
    cpu = (proc / "stat").read_text(encoding="utf-8").splitlines()[0].split()
    if cpu[0] != "cpu" or len(cpu) < len(CPU_NAMES) + 1:
        raise ValueError("Invalid aggregate CPU counters")
    ticks = os.sysconf("SC_CLK_TCK")
    cpu_times = {name: int(value) / ticks for name, value in zip(CPU_NAMES, cpu[1:])}

    memory = {}
    for row in (proc / "meminfo").read_text(encoding="utf-8").splitlines():
        key, _, value = row.partition(":")
        if key in MEMORY_NAMES:
            memory[MEMORY_NAMES[key]] = int(value.split()[0]) * 1024
    if len(memory) != len(MEMORY_NAMES):
        raise ValueError("Missing Linux memory counters")

    loads = (proc / "loadavg").read_text(encoding="utf-8").split()[:3]
    if len(loads) != 3:
        raise ValueError("Missing Linux load averages")
    return {
        "bootId": (proc / "sys/kernel/random/boot_id").read_text(encoding="utf-8").strip(),
        "cpuTimesSeconds": cpu_times,
        "memory": memory,
        "loadAverage": [float(value) for value in loads],
        "uptimeSeconds": float((proc / "uptime").read_text(encoding="utf-8").split()[0]),
        "filesystems": _filesystems(config.filesystems),
        "network": _network(config.network, network_root),
        "temperaturesCelsius": _temperatures(config.temperatures),
    }


def _filesystems(paths: dict[str, str]) -> dict[str, dict[str, int]]:
    filesystems = {}
    for alias, path in paths.items():
        try:
            info = os.statvfs(path)
        except OSError:
            LOG.warning("Filesystem sample unavailable for %s", alias)
            continue
        filesystems[alias] = {
            "totalBytes": info.f_blocks * info.f_frsize,
            "availableBytes": info.f_bavail * info.f_frsize,
        }
    return filesystems


def _network(interfaces: dict[str, str], root: Path) -> dict[str, dict[str, int]]:
    counters = {}
    for alias, name in interfaces.items():
        stats = root / name / "statistics"
        try:
            counters[alias] = {
                "rxBytes": int((stats / "rx_bytes").read_text(encoding="utf-8")),
                "txBytes": int((stats / "tx_bytes").read_text(encoding="utf-8")),
            }
        except (OSError, ValueError):
            LOG.warning("Network sample unavailable for %s", alias)
    return counters


def _temperatures(paths: dict[str, str]) -> dict[str, float]:
    values = {}
    for alias, path in paths.items():
        try:
            values[alias] = int(Path(path).read_text(encoding="utf-8")) / 1000
        except (OSError, ValueError):
            LOG.warning("Temperature sample unavailable for %s", alias)
    return values
