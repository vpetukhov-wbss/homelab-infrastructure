import json
import os
from base64 import b64encode
from http.client import HTTPConnection, HTTPException
from pathlib import Path
from urllib.parse import quote

from ..config import RabbitMQSettings
from .cached_probe import observe

PROBE_TIMEOUT_SECONDS = 1
MAX_RESPONSE_BYTES = 64 * 1024
MAX_PASSWORD_BYTES = 1024
FIELDS = {
    "messages": "messages",
    "messages_ready": "messagesReady",
    "messages_unacknowledged": "messagesUnacknowledged",
    "consumers": "consumers",
}


def collect(settings: RabbitMQSettings, cache_dir: Path | None) -> dict[str, object]:
    cache = cache_dir / "rabbitmq-queue.json" if cache_dir else None
    scope = [settings.host, settings.port, settings.vhost, settings.queue, settings.username, str(settings.password_file)]
    return observe(cache, scope, lambda: _probe(settings), _cached_metrics)


def _cached_metrics(reading: dict) -> dict[str, int]:
    return _metrics(reading, {name: name for name in FIELDS.values()})


def _metrics(reading: object, fields: dict[str, str]) -> dict[str, int]:
    if not isinstance(reading, dict):
        return {}
    metrics = {}
    for field, name in fields.items():
        value = reading.get(field)
        if type(value) is not int or not 0 <= value <= (1 << 64) - 1:
            return {}
        metrics[name] = value
    return metrics


def _password(settings: RabbitMQSettings) -> bytes:
    path = settings.password_file
    if path is None:
        directory = os.environ.get("CREDENTIALS_DIRECTORY")
        if not directory:
            raise ValueError("RabbitMQ credential directory is unavailable")
        path = Path(directory) / "rabbitmq-password"
    with path.open("rb") as stream:
        password = stream.read(MAX_PASSWORD_BYTES + 3).removesuffix(b"\n").removesuffix(b"\r")
    if not password or len(password) > MAX_PASSWORD_BYTES or any(byte in password for byte in (b"\x00", b"\r", b"\n")):
        raise ValueError("Invalid RabbitMQ credential")
    return password


def _probe(settings: RabbitMQSettings) -> dict[str, int]:
    credentials = settings.username.encode("ascii") + b":" + _password(settings)
    path = f"/api/queues/{quote(settings.vhost, safe='')}/{quote(settings.queue, safe='')}"
    path += "?columns=" + ",".join(FIELDS)
    connection = HTTPConnection(settings.host, settings.port, timeout=PROBE_TIMEOUT_SECONDS)
    try:
        connection.request("GET", path, headers={
            "Accept": "application/json",
            "Authorization": "Basic " + b64encode(credentials).decode("ascii"),
        })
        with connection.getresponse() as response:
            if response.status != 200:
                return {}
            body = response.read(MAX_RESPONSE_BYTES + 1)
        if len(body) > MAX_RESPONSE_BYTES:
            return {}
        return _metrics(json.loads(body), FIELDS)
    except (HTTPException, RecursionError):
        return {}
    finally:
        connection.close()
