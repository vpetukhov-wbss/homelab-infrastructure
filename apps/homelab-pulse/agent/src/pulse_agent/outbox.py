import fcntl
import os
import re
import tempfile
import time
from contextlib import contextmanager
from pathlib import Path

from .models import Sample

SAMPLE_FILENAME = re.compile(r"[0-9]{20}-[0-9a-f-]{36}\.json")


class Outbox:
    def __init__(self, directory: Path):
        self.directory = directory
        self.directory.mkdir(parents=True, mode=0o700, exist_ok=True)

    @contextmanager
    def sender(self):
        with (self.directory / ".sender.lock").open("a", encoding="utf-8") as lock:
            try:
                fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            except BlockingIOError:
                yield False
                return
            yield True

    def enqueue(self, sample: Sample) -> None:
        payload = sample.encode()
        target = self.directory / f"{time.time_ns():020d}-{sample.sample_id}.json"
        with tempfile.NamedTemporaryFile(dir=self.directory, prefix=".sample-", delete=False) as stream:
            temporary = Path(stream.name)
            try:
                stream.write(payload)
                stream.flush()
                os.fsync(stream.fileno())
                os.replace(temporary, target)
            finally:
                temporary.unlink(missing_ok=True)

    def pending(self) -> list[Path]:
        return sorted(path for path in self.directory.iterdir() if SAMPLE_FILENAME.fullmatch(path.name))

    def prune(self, max_pending: int, max_age: int) -> int:
        cutoff = time.time_ns() - max_age * 10**9
        files = self.pending()
        excess = max(0, len(files) - max_pending)
        removed = 0
        for index, path in enumerate(files):
            if index < excess or int(path.name[:20]) < cutoff:
                path.unlink()
                removed += 1
        return removed
