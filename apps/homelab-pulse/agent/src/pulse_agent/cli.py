import argparse
import configparser
import logging
from pathlib import Path

from .collect import tick
from .collectors import host
from .config import Config, read_token
from .models import Sample
from .transport import Transport


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="HomeLab Pulse Linux telemetry agent")
    parser.add_argument("--config", type=Path, default=Path("/etc/pulse-agent/agent.ini"))
    parser.add_argument("--token-file", type=Path, help="Path to a local credential file")
    parser.add_argument("command", choices=("sample", "tick"))
    args = parser.parse_args(argv)

    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(message)s")
    try:
        settings = Config.load(args.config)
        if args.command == "sample":
            print(Sample.host(host.collect(settings)).encode().decode("utf-8"))
            return 0
        token = read_token(args.token_file)
        return tick(settings, Transport(settings.ingest_url, token, settings.timeout_seconds))
    except (OSError, ValueError, KeyError, IndexError, configparser.Error):
        logging.error("Agent failed. Check its configuration, credential file and local metrics")
        return 1
