import platform
from pathlib import Path
from typing import TextIO

from .host import read_memory
from .smbios import read_memory_devices


def collect(
    proc: Path = Path("/proc"), dmi: Path = Path("/sys/class/dmi/id"),
    *, disks: dict[str, str] | None = None, block_root: Path = Path("/sys/block"),
    memory_report: TextIO | None = None,
) -> dict[str, object]:
    records = _cpu_records(proc / "cpuinfo")
    cpu = {"architecture": platform.machine(), "logicalCpuCount": len(records)}
    model = _label(next((record["model name"] for record in records if "model name" in record), ""))
    if model:
        cpu["model"] = model

    cores = {
        (record["physical id"], record["core id"])
        for record in records if "physical id" in record and "core id" in record
    }
    if cores and all("physical id" in record and "core id" in record for record in records):
        cpu["physicalCoreCount"] = len(cores)

    machine = {}
    for field, filename in (("manufacturer", "sys_vendor"), ("model", "product_name")):
        try:
            value = _label((dmi / filename).read_text(encoding="utf-8", errors="replace"))
        except OSError:
            continue
        if value:
            machine[field] = value

    memory: dict[str, object] = {"osVisibleBytes": read_memory(proc)["totalBytes"]}
    if memory_report is not None:
        modules = read_memory_devices(memory_report)
        if modules:
            memory["modules"] = modules

    profile: dict[str, object] = {
        "machine": machine,
        "cpu": cpu,
        "memory": memory,
    }
    if disks:
        profile["disks"] = {alias: _disk(block_root / name) for alias, name in disks.items()}
    return profile


def _disk(path: Path) -> dict[str, object]:
    try:
        if not (path / "device").is_dir():
            return {"status": "unavailable"}
        capacity = int(_read_attribute(path / "size")) * 512  # Linux uses 512-byte sectors here.
    except (OSError, ValueError):
        return {"status": "unavailable"}
    if capacity <= 0:
        return {"status": "unavailable"}

    disk: dict[str, object] = {"status": "available", "capacityBytes": capacity}
    model = _label(_read_attribute(path / "device/model"))
    if model:
        disk["model"] = model
    rotational = _read_attribute(path / "queue/rotational")
    if rotational in {"0", "1"}:
        disk["rotational"] = rotational == "1"
    return disk


def _read_attribute(path: Path) -> str:
    try:
        return path.read_text(encoding="utf-8", errors="replace").strip()
    except OSError:
        return ""


def _cpu_records(path: Path) -> list[dict[str, str]]:
    records = []
    for block in path.read_text(encoding="utf-8", errors="replace").split("\n\n"):
        fields = {}
        for line in block.splitlines():
            name, separator, value = line.partition(":")
            if separator:
                fields[name.strip()] = value.strip()
        if fields.get("processor", "").isdecimal():
            records.append(fields)
    if not records:
        raise ValueError("Missing Linux CPU inventory")
    return records


def _label(value: str) -> str:
    label = " ".join(value.split())
    return label[:128] if label.isprintable() else ""
