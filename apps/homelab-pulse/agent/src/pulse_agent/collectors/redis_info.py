import os
import socket
from pathlib import Path
from time import monotonic
from typing import BinaryIO

from ..config import RedisSettings
from .cached_probe import observe

PROBE_SECONDS = 1
MAX_INFO_BYTES = 64 * 1024
FIELDS = {
    b"used_memory": "usedMemoryBytes",
    b"maxmemory": "maxMemoryBytes",
    b"instantaneous_ops_per_sec": "operationsPerSecond",
    b"keyspace_hits": "keyspaceHitsTotal",
    b"keyspace_misses": "keyspaceMissesTotal",
    b"evicted_keys": "evictedKeysTotal",
    b"connected_clients": "connectedClients",
}


def collect(settings: RedisSettings, cache_dir: Path | None) -> dict[str, object]:
    cache = cache_dir / "redis-info.json" if cache_dir else None
    scope = [settings.host, settings.port, settings.username, str(settings.password_file)]
    return observe(cache, scope, lambda: _metrics(_probe(settings)), _cached_metrics)


def _cached_metrics(reading: dict) -> dict[str, int]:
    fields = (*FIELDS.values(), "aofCurrentSizeBytes")
    metrics = {
        name: reading[name] for name in fields if type(reading.get(name)) is int and reading[name] >= 0
    }
    return metrics if "usedMemoryBytes" in metrics else {}


def _password(settings: RedisSettings) -> bytes:
    path = settings.password_file
    if path is None:
        directory = os.environ.get("CREDENTIALS_DIRECTORY")
        if not directory:
            raise ValueError("Redis credential directory is unavailable")
        path = Path(directory) / "redis-password"
    with path.open("rb") as stream:
        password = stream.read(1025).rstrip(b"\r\n")
    if not password or len(password) > 1024 or b"\x00" in password:
        raise ValueError("Invalid Redis credential")
    return password


def _probe(settings: RedisSettings) -> bytes:
    password = _password(settings)
    deadline = monotonic() + PROBE_SECONDS
    with socket.create_connection((settings.host, settings.port), timeout=PROBE_SECONDS) as client:
        with client.makefile("rb") as stream:
            _send(client, deadline, b"AUTH", settings.username.encode("ascii"), password)
            if _header(client, stream, deadline) != b"+OK\r\n":
                raise ValueError("Redis authentication failed")
            _send(client, deadline, b"INFO", b"memory", b"stats", b"persistence", b"clients")
            header = _header(client, stream, deadline)
            length = header[1:-2]
            if not header.startswith(b"$") or not length.isdigit() or int(length) > MAX_INFO_BYTES:
                raise ValueError("Invalid Redis INFO reply")
            payload = bytearray()
            size = int(length) + 2
            while len(payload) < size:
                client.settimeout(_remaining(deadline))
                chunk = stream.read1(min(4096, size - len(payload)))
                if not chunk:
                    raise ValueError("Truncated Redis INFO reply")
                payload.extend(chunk)
            if payload[-2:] != b"\r\n":
                raise ValueError("Invalid Redis INFO reply")
            return bytes(payload[:-2])


def _send(client: socket.socket, deadline: float, *args: bytes) -> None:
    command = [b"*%d\r\n" % len(args)]
    command.extend(b"$%d\r\n%s\r\n" % (len(arg), arg) for arg in args)
    client.settimeout(_remaining(deadline))
    client.sendall(b"".join(command))


def _header(client: socket.socket, stream: BinaryIO, deadline: float) -> bytes:
    line = bytearray()
    for _ in range(64):
        client.settimeout(_remaining(deadline))
        byte = stream.read(1)
        if not byte:
            break
        line.extend(byte)
        if line.endswith(b"\r\n"):
            return bytes(line)
    raise ValueError("Invalid Redis reply header")


def _remaining(deadline: float) -> float:
    seconds = deadline - monotonic()
    if seconds <= 0:
        raise TimeoutError
    return seconds


def _metrics(info: bytes) -> dict[str, int]:
    pairs = (line.partition(b":") for line in info.splitlines())
    fields = {key: value for key, separator, value in pairs if separator}
    selected = dict(FIELDS)
    if fields.get(b"aof_enabled") == b"1":
        selected[b"aof_current_size"] = "aofCurrentSizeBytes"
    metrics = {}
    for key, name in selected.items():
        raw = fields.get(key, b"")
        if raw.isdigit() and len(raw) <= 20:
            metrics[name] = int(raw)
    return metrics if "usedMemoryBytes" in metrics else {}
