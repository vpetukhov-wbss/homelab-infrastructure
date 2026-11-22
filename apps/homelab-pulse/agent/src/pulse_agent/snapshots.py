import json
import os
import tempfile
from pathlib import Path


def write_snapshot(path: Path, snapshot: dict[str, object]) -> None:
    fd, name = tempfile.mkstemp(dir=path.parent, prefix=".snapshot-")
    temporary = Path(name)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            json.dump(snapshot, stream)
            stream.write("\n")
            os.fchmod(stream.fileno(), 0o640)
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)
