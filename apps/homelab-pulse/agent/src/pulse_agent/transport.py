from enum import Enum, auto
from urllib.error import HTTPError, URLError
from urllib.request import HTTPRedirectHandler, ProxyHandler, Request, build_opener


class Outcome(Enum):
    ACK = auto()
    REJECTED = auto()
    RETRY = auto()


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class Transport:
    def __init__(self, endpoint: str, token: str, timeout: float):
        self.endpoint = endpoint
        self.token = token
        self.timeout = timeout
        self.opener = build_opener(ProxyHandler({}), NoRedirect())

    def send(self, payload: bytes) -> Outcome:
        request = Request(
            self.endpoint,
            data=payload,
            method="POST",
            headers={
                "Content-Type": "application/json",
                "Authorization": f"Bearer {self.token}",
            },
        )
        try:
            with self.opener.open(request, timeout=self.timeout) as response:
                return Outcome.ACK if response.status == 204 else Outcome.RETRY
        except HTTPError as error:
            error.close()
            if error.code in {400, 413, 422}:
                return Outcome.REJECTED
            return Outcome.RETRY
        except (URLError, OSError):
            return Outcome.RETRY
