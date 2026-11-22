import json
import os
import subprocess
from datetime import datetime
from pathlib import Path

from ..config import PostgreSQLSettings
from .cached_probe import observe

PSQL = "/usr/bin/psql"
QUERY = """
SELECT pg_catalog.row_to_json(sample)
FROM (
    SELECT pg_catalog.pg_is_in_recovery() AS "inRecovery",
           pg_catalog.pg_database_size(datid) AS "databaseBytes",
           numbackends AS "connectedBackends",
           xact_commit AS "commitsTotal",
           xact_rollback AS "rollbacksTotal",
           blks_hit AS "bufferHitsTotal",
           blks_read AS "blocksReadTotal",
           deadlocks AS "deadlocksTotal",
           pg_catalog.to_char(stats_reset AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"') AS "statsResetAt"
    FROM pg_catalog.pg_stat_database
    WHERE datname = pg_catalog.current_database()
) AS sample
"""
COUNTERS = (
    "databaseBytes", "connectedBackends", "commitsTotal", "rollbacksTotal",
    "bufferHitsTotal", "blocksReadTotal", "deadlocksTotal",
)


def collect(settings: PostgreSQLSettings, cache_dir: Path | None) -> dict[str, object]:
    cache = cache_dir / "postgresql-stats.json" if cache_dir else None
    scope = [settings.host, settings.port, settings.database, settings.username, str(settings.pgpass_file)]
    return observe(cache, scope, lambda: _probe(settings), _metrics)


def _probe(settings: PostgreSQLSettings) -> dict[str, object]:
    password_file = settings.pgpass_file
    if password_file is None:
        directory = os.environ.get("CREDENTIALS_DIRECTORY")
        if not directory:
            return {}
        password_file = Path(directory) / "postgres-pgpass"
    if not password_file.is_file():
        return {}
    env = {
        "LANG": "C",
        "PATH": "/usr/bin:/bin",
        "PGHOST": settings.host,
        "PGHOSTADDR": settings.host,
        "PGPORT": str(settings.port),
        "PGDATABASE": settings.database,
        "PGUSER": settings.username,
        "PGPASSFILE": str(password_file),
        "PGCONNECT_TIMEOUT": "2",
        "PGSSLMODE": "prefer",
        "PGOPTIONS": "-c default_transaction_read_only=on -c statement_timeout=1000 -c lock_timeout=500",
    }
    try:
        result = subprocess.run(
            [PSQL, "-X", "-A", "-t", "-w", "-v", "ON_ERROR_STOP=1", "-c", QUERY],
            env=env, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
            timeout=2, check=False,
        )
        if result.returncode == 0 and 0 < len(result.stdout) <= 4096:
            return _metrics(json.loads(result.stdout))
    except (subprocess.TimeoutExpired, RecursionError):
        pass
    return {}


def _metrics(data: dict) -> dict[str, object]:
    if not isinstance(data, dict) or type(data.get("inRecovery")) is not bool:
        return {}
    if any(type(data.get(field)) is not int or data[field] < 0 for field in COUNTERS):
        return {}
    metrics = {field: data[field] for field in (*COUNTERS, "inRecovery")}
    reset = data.get("statsResetAt")
    if isinstance(reset, str) and reset.endswith("Z"):
        try:
            parsed = datetime.fromisoformat(reset[:-1] + "+00:00")
        except ValueError:
            pass
        else:
            metrics["statsResetAt"] = parsed.isoformat(timespec="microseconds").replace("+00:00", "Z")
    return metrics
