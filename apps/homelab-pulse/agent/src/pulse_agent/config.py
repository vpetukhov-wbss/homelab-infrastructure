import configparser
import os
import re
from dataclasses import dataclass, field
from ipaddress import ip_address, ip_network
from pathlib import Path
from urllib.parse import urlsplit

PRIVATE_MONITORING_NETWORKS = tuple(map(ip_network, ("10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7")))


@dataclass(frozen=True)
class RedisSettings:
    host: str
    port: int
    username: str
    password_file: Path | None


@dataclass(frozen=True)
class PostgreSQLSettings:
    host: str
    port: int
    database: str
    username: str
    pgpass_file: Path | None


@dataclass(frozen=True)
class RabbitMQSettings:
    host: str
    port: int
    vhost: str
    queue: str
    username: str
    password_file: Path | None


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
    sqlite_databases: dict[str, str] = field(default_factory=dict)
    redis: RedisSettings | None = None
    postgresql: PostgreSQLSettings | None = None
    rabbitmq: RabbitMQSettings | None = None
    gitea_health_url: str | None = None

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
        sqlite_databases = _aliases(parser, "sqlite_databases")
        local_paths = (*filesystems.values(), *temperatures.values(), *fans.values(), *smart_snapshots.values())
        if not all(Path(value).is_absolute() for value in local_paths):
            raise ValueError("Filesystem and sensor paths must be absolute")
        if not all(Path(value).is_absolute() for value in sqlite_databases.values()):
            raise ValueError("SQLite database paths must be absolute")
        if not all(re.fullmatch(r"[A-Za-z0-9_-]{1,15}", name) for name in network.values()):
            raise ValueError("Invalid interface name")
        if not all(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9_-]{0,63}", name) for name in disks.values()):
            raise ValueError("Invalid block device name")

        return cls(
            agent["ingest_url"], path.parent / agent.get("state_directory", "./data/pulse-agent"),
            timeout, max_pending, max_age, send_limit, filesystems, network, temperatures, disks, fans,
            smart_snapshots, _private_probe_url(agent.get("nginx_status_url", ""), "NGINX status"), sqlite_databases,
            _redis_settings(parser), _postgresql_settings(parser), _rabbitmq_settings(parser),
            _private_probe_url(agent.get("gitea_health_url", ""), "Gitea health", suffix="/api/healthz"),
        )


def _aliases(parser: configparser.ConfigParser, section: str) -> dict[str, str]:
    values = dict(parser.items(section)) if parser.has_section(section) else {}
    if not all(re.fullmatch(r"[a-z][a-z0-9_-]{0,31}", alias) for alias in values):
        raise ValueError(f"Invalid alias in [{section}]")
    return values


def _private_probe_url(value: str, label: str, *, suffix: str = "") -> str | None:
    if not value:
        return None
    url = urlsplit(value)
    if (
        any(ord(char) <= 32 or ord(char) == 127 for char in value)
        or url.scheme not in {"http", "https"} or url.username is not None or url.password is not None
        or not url.path or not url.path.endswith(suffix) or "?" in value or "#" in value or url.port == 0
    ):
        raise ValueError(f"Invalid {label} URL")
    if not _private_ip_literal(url.hostname or ""):
        raise ValueError(f"{label} URL requires a private IP literal")
    return value


def _private_ip_literal(value: str) -> bool:
    try:
        address = ip_address(value)
    except ValueError:
        return False
    return address.is_loopback or any(address in network for network in PRIVATE_MONITORING_NETWORKS)


def _redis_settings(parser: configparser.ConfigParser) -> RedisSettings | None:
    if not parser.has_section("redis"):
        return None
    section = parser["redis"]
    host = section.get("host", "")
    if not host:
        return None
    if not _private_ip_literal(host):
        raise ValueError("Redis requires a private IP literal")
    port = section.getint("port", fallback=6379)
    username = section.get("username", "pulse-monitor")
    if not 1 <= port <= 65535 or not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", username) or username == "default":
        raise ValueError("Redis requires a valid port and a dedicated ACL username")
    path = section.get("password_file")
    password_file = Path(path) if path else None
    if password_file is not None and not password_file.is_absolute():
        raise ValueError("Redis password_file must be absolute")
    return RedisSettings(host, port, username, password_file)


def _postgresql_settings(parser: configparser.ConfigParser) -> PostgreSQLSettings | None:
    if not parser.has_section("postgresql"):
        return None
    section = parser["postgresql"]
    host = section.get("host", "")
    if not host:
        return None
    if not _private_ip_literal(host):
        raise ValueError("PostgreSQL requires a private IP literal")
    port = section.getint("port", fallback=5432)
    database = section.get("database", "postgres")
    username = section.get("username", "pulse-monitor")
    if (
        not 1 <= port <= 65535 or not re.fullmatch(r"[A-Za-z0-9_.-]{1,63}", database)
        or not re.fullmatch(r"[A-Za-z0-9_.-]{1,63}", username) or username == "postgres"
    ):
        raise ValueError("PostgreSQL requires a valid port, database and dedicated monitor username")
    path = section.get("pgpass_file")
    pgpass_file = Path(path) if path else None
    if pgpass_file is not None and not pgpass_file.is_absolute():
        raise ValueError("PostgreSQL pgpass_file must be absolute")
    return PostgreSQLSettings(host, port, database, username, pgpass_file)


def _rabbitmq_settings(parser: configparser.ConfigParser) -> RabbitMQSettings | None:
    if not parser.has_section("rabbitmq"):
        return None
    section = parser["rabbitmq"]
    host = section.get("host", "")
    if not host:
        return None
    if not _private_ip_literal(host):
        raise ValueError("RabbitMQ requires a private IP literal")
    port = section.getint("port", fallback=15672)
    username = section.get("username", "pulse-monitor")
    if not 1 <= port <= 65535 or not re.fullmatch(r"[A-Za-z0-9_.-]{1,64}", username) or username == "guest":
        raise ValueError("RabbitMQ requires a valid port and a dedicated monitor username")
    vhost, queue = section.get("vhost", "/"), section.get("queue", "")
    if any(
        not name or not name.isprintable() or len(name.encode("utf-8")) > 255 or name in {".", ".."}
        for name in (vhost, queue)
    ):
        raise ValueError("RabbitMQ requires a valid vhost and queue name")
    path = section.get("password_file")
    password_file = Path(path) if path else None
    if password_file is not None and not password_file.is_absolute():
        raise ValueError("RabbitMQ password_file must be absolute")
    return RabbitMQSettings(host, port, vhost, queue, username, password_file)


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
