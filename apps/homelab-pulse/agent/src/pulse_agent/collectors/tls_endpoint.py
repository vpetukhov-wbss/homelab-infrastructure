import socket
import ssl
from datetime import datetime, timezone
from pathlib import Path
from time import monotonic

from ..config import TLSSettings
from .cached_probe import observe

NETWORK_TIMEOUT_SECONDS = 2


def collect(settings: TLSSettings, cache_dir: Path | None) -> dict[str, object]:
    cache = cache_dir / "tls-expiry.json" if cache_dir else None
    scope = [settings.host, settings.port, settings.server_name, str(settings.ca_file or "")]
    return observe(cache, scope, lambda: _probe(settings), _metrics)


def _metrics(reading: dict) -> dict[str, str]:
    expiry = reading.get("expiresAt")
    if not isinstance(expiry, str) or not expiry.endswith("Z"):
        return {}
    parsed = datetime.fromisoformat(expiry[:-1] + "+00:00")
    if parsed.utcoffset() is None:
        return {}
    return {"expiresAt": parsed.isoformat(timespec="seconds").replace("+00:00", "Z")}


def _probe(settings: TLSSettings) -> dict[str, str]:
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    if settings.ca_file is None:
        context.load_default_certs()
    else:
        context.load_verify_locations(cafile=str(settings.ca_file))

    deadline = monotonic() + NETWORK_TIMEOUT_SECONDS
    with socket.create_connection((settings.host, settings.port), timeout=NETWORK_TIMEOUT_SECONDS) as connection:
        remaining = deadline - monotonic()
        if remaining <= 0:
            return {}
        connection.settimeout(remaining)
        with context.wrap_socket(connection, server_hostname=settings.server_name) as tls:
            certificate = tls.getpeercert()
            not_after = certificate.get("notAfter") if certificate else None
    if not_after is None:
        return {}

    expires_at = datetime.fromtimestamp(ssl.cert_time_to_seconds(not_after), timezone.utc)
    return {"expiresAt": expires_at.isoformat(timespec="seconds").replace("+00:00", "Z")}
