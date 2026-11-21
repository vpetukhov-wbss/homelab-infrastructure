import json
from dataclasses import dataclass
from datetime import datetime, timezone
from uuid import uuid4

MAX_SAMPLE_BYTES = 64 * 1024


@dataclass(frozen=True)
class Sample:
    sample_id: str
    observed_at: datetime
    values: dict[str, object]

    @classmethod
    def host(cls, values: dict[str, object]) -> "Sample":
        return cls(str(uuid4()), datetime.now(timezone.utc), values)

    def encode(self) -> bytes:
        data = {
            "schemaVersion": 1,
            "sampleId": self.sample_id,
            "observedAt": self.observed_at.isoformat(timespec="milliseconds").replace("+00:00", "Z"),
            "kind": "host",
            "values": self.values,
        }
        payload = json.dumps(data, separators=(",", ":"), allow_nan=False).encode("utf-8")
        if len(payload) > MAX_SAMPLE_BYTES:
            raise ValueError("Sample exceeds the payload limit")
        return payload
