import configparser
import os
import re
from dataclasses import dataclass, field
from ipaddress import ip_address, ip_network
from pathlib import Path
from urllib.parse import urlsplit

PRIVATE_MONITORING_NETWORKS = tuple(map(ip_network, ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7")))


@dataclass(frozen=True)
class Config:
    ingest_url: str
    state_directory: Path
    timeout_seconds: float
    max_pending: int
    max_age_seconds: int
    send_limit: int
    filesystems: dict[str, str]
    network: dict[str, str]
    temperatures: dict[str, str]
    disks: dict[str, str] = field(default_factory=dict)
    fans: dict[str, str] = field(default_factory=dict)
    smart_snapshots: dict[str, str] = field(default_factory=dict)
    nginx_status_url: str | None = None

    @classmethod
    def load(cls, path: Path) -> "Config":
        parser = configparser.ConfigParser(interpolation=None)
        with path.open(encoding="utf-8") as stream:
            parser.read_file(stream)

        agent = parser["agent"]
        url = urlsplit(agent["ingest_url"])
        if (
            url.scheme not in {"http", "https"} or not url.hostname
            or url.username is not None or url.password is not None
            or url.path != "/internal/v1/telemetry" or url.query or url.fragment
        ):
            raise ValueError("ingest_url must point to /internal/v1/telemetry")

        timeout = agent.getfloat("timeout_seconds", fallback=2)
        max_pending = agent.getint("max_pending", fallback=512)
        max_age = agent.getint("max_age_seconds", fallback=86400)
        send_limit = agent.getint("send_limit", fallback=4)
        if not 0 < timeout <= 5 or max_pending < 1 or max_age < 1 or not 1 <= send_limit <= 8:
            raise ValueError("Invalid delivery or queue limits")

        filesystems = _aliases(parser, "filesystems")
        temperatures = _aliases(parser, "temperatures")
        fans = _aliases(parser, "fans")
        network = _aliases(parser, "network")
        disks = _aliases(parser, "disks")
        smart_snapshots = _aliases(parser, "smart_snapshots")
        local_paths = (*filesystems.values(), *temperatures.values(), *fans.values(), *smart_snapshots.values())
        if not all(Path(value).is_absolute() for value in local_paths):
            raise ValueError("Filesystem and sensor paths must be absolute")
        if not all(re.fullmatch(r"[A-Za-z0-9_-]{1,15}", name) for name in network.values()):
            raise ValueError("Invalid interface name")
        if not all(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]{0,63}", name) for name in disks.values()):
            raise ValueError("Invalid block device name")

        return cls(
            agent["ingest_url"], path.parent / agent.get("state_directory", "./data/pulse-agent"),
            timeout, max_pending, max_age, send_limit, filesystems, network, temperatures, disks, fans,
            smart_snapshots, _nginx_status_url(agent.get("nginx_status_url", "")),
        )


def _aliases(parser: configparser.ConfigParser, section: str) -> dict[str, str]:
    values = dict(parser.items(section)) if parser.has_section(section) else {}
    if not all(re.fullmatch(r"[a-z][a-z0-9_-]{0,31}", alias) for alias in values):
        raise ValueError(f"Invalid alias in [{section}]")
    return values


def _nginx_status_url(value: str) -> str | None:
    if not value:
        return None
    url = urlsplit(value)
    if (
        any(ord(char) <= 32 or ord(char) == 127 for char in value)
        or url.scheme not in {"http", "https"} or url.username is not None or url.password is not None
        or not url.path or "?" in value or "#" in value or url.port == 0
    ):
        raise ValueError("Invalid NGINX status URL")
    try:
        address = ip_address(url.hostname or "")
    except ValueError:
        raise ValueError("NGINX status URL requires a private IP literal") from None
    if not address.is_loopback and not any(address in network for network in PRIVATE_MONITORING_NETWORKS):
        raise ValueError("NGINX status URL requires a private IP literal")
    return value


def read_token(path: Path | None) -> str:
    if path is None:
        directory = os.environ.get("CREDENTIALS_DIRECTORY")
        if not directory:
            raise ValueError("Provide --token-file or a systemd ingest-token credential")
        path = Path(directory) / "ingest-token"
    token = path.read_text(encoding="utf-8").strip()
    if not token or not token.isascii() or not token.isprintable() or " " in token:
        raise ValueError("Invalid ingest token")
    return token
