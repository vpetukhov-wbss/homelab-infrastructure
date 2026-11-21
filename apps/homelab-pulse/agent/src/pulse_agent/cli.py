import argparse
import configparser
import json
import logging
import sys
from pathlib import Path

from .collect import tick
from .collectors import host, inventory, smart
from .config import Config, read_token
from .models import Sample
from .transport import Transport


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="HomeLab Pulse Linux telemetry agent")
    parser.add_argument("--config", type=Path, help="Agent configuration file")
    parser.add_argument("--token-file", type=Path, help="Path to a local credential file")
    parser.add_argument(
        "--smbios-memory", action="store_true", help="Read dmidecode type 17 from stdin for inventory",
    )
    parser.add_argument("command", choices=("sample", "inventory", "smart-snapshot", "tick"))
    args = parser.parse_args(argv)
    if args.smbios_memory and args.command != "inventory":
        parser.error("--smbios-memory requires inventory")

    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s")
    try:
        if args.command == "smart-snapshot":
            print(json.dumps(smart.normalize(sys.stdin)))
            return 0
        if args.command == "inventory":
            disks = Config.load(args.config).disks if args.config else {}
            report = sys.stdin if args.smbios_memory else None
            print(json.dumps(inventory.collect(disks=disks, memory_report=report), indent=2))
            return 0
        settings = Config.load(args.config or Path("/etc/pulse-agent/agent.ini"))
        if args.command == "sample":
            print(Sample.host(host.collect(settings)).encode().decode("utf-8"))
            return 0
        token = read_token(args.token_file)
        return tick(settings, Transport(settings.ingest_url, token, settings.timeout_seconds))
    except (OSError, ValueError, KeyError, IndexError, RecursionError, configparser.Error):
        logging.error("Agent failed. Check its configuration, credential file and local metrics")
        return 1
