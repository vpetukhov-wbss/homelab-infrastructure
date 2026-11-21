import re
from typing import TextIO

MAX_REPORT_CHARS = 1024 * 1024
MEMORY_DEVICE_HEADER = re.compile(r"Handle 0x[0-9a-fA-F]+, DMI type 17, [0-9]+ bytes")
CAPACITY_UNITS = {"K": 1024, "M": 1024**2, "G": 1024**3, "T": 1024**4}
MEMORY_TYPES = frozenset({
    "DRAM", "EDRAM", "VRAM", "SRAM", "RAM", "CDRAM", "3DRAM", "SDRAM", "SGRAM", "RDRAM",
    "DDR", "DDR2", "DDR2 FB-DIMM", "DDR3", "FBD2", "DDR4", "DDR5",
    "LPDDR", "LPDDR2", "LPDDR3", "LPDDR4", "LPDDR5", "HBM", "HBM2", "HBM3",
})


def read_memory_devices(source: TextIO) -> list[dict[str, int | str]]:
    report = source.read(MAX_REPORT_CHARS + 1)
    if len(report) > MAX_REPORT_CHARS:
        raise ValueError("SMBIOS memory report exceeds the input limit")

    modules = []
    for block in re.split(r"(?m)(?=^Handle )", report):
        lines = block.splitlines()
        if not lines or not MEMORY_DEVICE_HEADER.fullmatch(lines[0]):
            continue
        fields = {}
        for line in lines[1:]:
            key, _, value = line.strip().partition(":")
            if key in {"Size", "Type", "Speed", "Configured Memory Speed", "Configured Clock Speed"}:
                fields[key] = value.strip()
        size = re.fullmatch(r"([1-9][0-9]{0,9})\s+([KMGT])i?B", fields.get("Size", ""))
        if size is None:
            continue

        amount, unit = size.groups()
        module: dict[str, int | str] = {"capacityBytes": int(amount) * CAPACITY_UNITS[unit]}
        memory_type = fields.get("Type")
        if memory_type in MEMORY_TYPES:
            module["type"] = memory_type
        configured_speed = fields.get("Configured Memory Speed", fields.get("Configured Clock Speed", ""))
        for field, value in (
            ("ratedSpeedMtPerSecond", fields.get("Speed", "")),
            ("configuredSpeedMtPerSecond", configured_speed),
        ):
            rate = re.fullmatch(r"([1-9][0-9]{0,9})\s+MT/s", value)
            if rate:
                module[field] = int(rate[1])
        modules.append(module)
    return modules
