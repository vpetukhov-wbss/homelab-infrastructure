import platform
from pathlib import Path

from .host import read_memory


def collect(
    proc: Path = Path("/proc"), dmi: Path = Path("/sys/class/dmi/id"),
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

    return {
        "machine": machine,
        "cpu": cpu,
        "memory": {"osVisibleBytes": read_memory(proc)["totalBytes"]},
    }


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
