import json
from http.client import HTTPException
from pathlib import Path
from time import monotonic
from urllib.error import HTTPError
from urllib.request import ProxyHandler, Request, build_opener

from ..transport import NoRedirect
from .cached_probe import observe

MAX_RESPONSE_BYTES = 8 * 1024
SOCKET_TIMEOUT_SECONDS = 1


def collect(url: str, cache_dir: Path | None) -> dict[str, object]:
    cache = cache_dir / "gitea-health.json" if cache_dir else None
    return observe(cache, [url], lambda: _probe(url), _metrics)


def _metrics(reading: dict) -> dict[str, int | bool]:
    if any(type(reading.get(name)) is not bool for name in ("databaseHealthy", "cacheHealthy")):
        return {}
    latency = reading.get("responseTimeMs")
    if type(latency) is not int or latency < 0:
        return {}
    return {
        "databaseHealthy": reading["databaseHealthy"],
        "cacheHealthy": reading["cacheHealthy"],
        "responseTimeMs": latency,
    }


def _healthy(checks: dict, name: str) -> bool:
    entries = checks[name]
    if not isinstance(entries, list) or len(entries) != 1 or entries[0]["status"] not in ("pass", "fail"):
        raise ValueError("Invalid Gitea component health")
    return entries[0]["status"] == "pass"


def _probe(url: str) -> dict[str, int | bool]:
    opener = build_opener(ProxyHandler({}), NoRedirect())
    start = monotonic()
    try:
        try:
            response = opener.open(Request(url, headers={"Accept": "application/json"}), timeout=SOCKET_TIMEOUT_SECONDS)
        except HTTPError as error:
            response = error
        with response:
            status = response.getcode()
            if status not in (200, 424):
                return {}
            body = response.read(MAX_RESPONSE_BYTES + 1)
        elapsed_ms = round((monotonic() - start) * 1000)
        if len(body) > MAX_RESPONSE_BYTES:
            return {}
        data = json.loads(body)
        database = _healthy(data["checks"], "database:ping")
        cache = _healthy(data["checks"], "cache:ping")
        expected = (200, "pass") if database and cache else (424, "fail")
        if (status, data["status"]) == expected:
            return {"databaseHealthy": database, "cacheHealthy": cache, "responseTimeMs": elapsed_ms}
    except (HTTPException, KeyError, TypeError, RecursionError):
        pass
    return {}
