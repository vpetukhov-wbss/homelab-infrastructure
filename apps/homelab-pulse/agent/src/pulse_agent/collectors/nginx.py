import re
from datetime import datetime, timezone
from http.client import HTTPException
from urllib.error import HTTPError
from urllib.request import ProxyHandler, Request, build_opener

from ..transport import NoRedirect

MAX_STATUS_BYTES = 1024
STATUS_TIMEOUT_SECONDS = 1
STUB_STATUS = re.compile(
    rb"Active connections:[ \t]+(?P<activeConnections>[0-9]{1,20})[ \t]*\r?\n"
    rb"[ \t]*server accepts handled requests[ \t]*\r?\n"
    rb"[ \t]*(?P<acceptedConnectionsTotal>[0-9]{1,20})[ \t]+"
    rb"(?P<handledConnectionsTotal>[0-9]{1,20})[ \t]+(?P<requestsTotal>[0-9]{1,20})[ \t]*\r?\n"
    rb"Reading:[ \t]+(?P<readingConnections>[0-9]{1,20})[ \t]+"
    rb"Writing:[ \t]+(?P<writingConnections>[0-9]{1,20})[ \t]+"
    rb"Waiting:[ \t]+(?P<waitingConnections>[0-9]{1,20})"
)


def collect(url: str) -> dict[str, object]:
    metrics = _read_status(url)
    return {
        "status": "available" if metrics else "unavailable",
        "observedAt": datetime.now(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z"),
        **metrics,
    }


def _read_status(url: str) -> dict[str, int]:
    opener = build_opener(ProxyHandler({}), NoRedirect())
    try:
        with opener.open(Request(url), timeout=STATUS_TIMEOUT_SECONDS) as response:
            if response.status != 200:
                return {}
            body = response.read(MAX_STATUS_BYTES + 1)
    except HTTPError as error:
        error.close()
        return {}
    except (OSError, HTTPException):
        return {}

    if len(body) > MAX_STATUS_BYTES:
        return {}
    match = STUB_STATUS.fullmatch(body.rstrip())
    return {name: int(value) for name, value in match.groupdict().items()} if match else {}
