"""Summarize the Gitea 1.22 Actions tables through a read-only SQLite connection."""

import argparse
import json
import sqlite3
from contextlib import closing
from datetime import datetime, timezone
from pathlib import Path
from time import monotonic

from .collectors.gitea_actions import COUNT_KEYS, metrics
from .snapshots import write_snapshot

# Gitea 1.22.6 models/actions/status.go and models/actions/runner.go.
QUEUE = """
SELECT count(CASE WHEN status=5 THEN 1 END),
       count(CASE WHEN status=6 THEN 1 END),
       count(CASE WHEN status=7 THEN 1 END)
  FROM action_run_job WHERE status IN (5,6,7)
"""
RECENT = """
SELECT count(CASE WHEN status=1 THEN 1 END),
       count(CASE WHEN status=2 THEN 1 END),
       count(CASE WHEN status=3 THEN 1 END),
       count(CASE WHEN status=4 THEN 1 END),
       count(duration), CAST(avg(duration) AS INTEGER)
  FROM (
      SELECT status, CASE WHEN started > 0 AND stopped BETWEEN started AND :now
                          THEN stopped-started END AS duration
        FROM action_run_job
       WHERE status IN (1,2,3,4) AND updated BETWEEN :cutoff AND :now
  )
"""
RUNNERS = """
SELECT count(CASE WHEN last_online >= :online AND last_active >= :active THEN 1 END),
       count(CASE WHEN last_online >= :online AND coalesce(last_active,0) < :active THEN 1 END),
       count(CASE WHEN coalesce(last_online,0) < :online THEN 1 END)
  FROM action_runner WHERE deleted IS NULL OR deleted=0
"""


def _read(db: Path, now: int) -> dict[str, int]:
    deadline = monotonic() + 1
    try:
        with closing(sqlite3.connect(db.as_uri() + "?mode=ro", uri=True, timeout=0.2)) as connection:
            connection.set_progress_handler(lambda: int(monotonic() >= deadline), 1000)
            connection.execute("PRAGMA query_only=ON")
            connection.execute("PRAGMA trusted_schema=OFF")
            connection.execute("BEGIN")
            queue = connection.execute(QUEUE).fetchone()
            outcomes = connection.execute(RECENT, {"now": now, "cutoff": now - 3600}).fetchone()
            runners = connection.execute(RUNNERS, {"online": now - 60, "active": now - 10}).fetchone()
    except sqlite3.Error:
        return {}
    values = dict(zip(COUNT_KEYS, (*queue, *outcomes[:4], *runners, outcomes[4])))
    values["meanJobDurationSecondsLastHour"] = outcomes[5]
    return metrics(values)


def refresh(database: Path, output: Path) -> None:
    if not database.is_absolute():
        raise ValueError("Gitea Actions database path must be absolute")
    now = datetime.now(timezone.utc)
    values = _read(database, int(now.timestamp()))
    observed = now.isoformat(timespec="seconds").replace("+00:00", "Z")
    write_snapshot(output, {"status": "available" if values else "unavailable", "observedAt": observed, **values})


def main() -> int:
    parser = argparse.ArgumentParser(description="Summarize local Gitea Actions usage")
    parser.add_argument("settings", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    try:
        path = json.loads(args.settings.read_bytes())
        if not isinstance(path, str):
            raise ValueError("Expected an absolute Gitea database path")
        refresh(Path(path), args.output)
    except (OSError, ValueError, RecursionError):
        parser.exit(1, "Gitea Actions snapshot could not be updated\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
