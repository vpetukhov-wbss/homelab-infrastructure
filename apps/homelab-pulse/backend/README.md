# HomeLab Pulse receiver

This ASP.NET Core 10 receiver accepts telemetry from the Python agent and stores it
in SQLite. It is the first backend slice, with no deployment configuration.

## Local development

Use the .NET SDK pinned in `global.json` and Python 3.10 or newer. Run these checks
from this directory:

```sh
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
dotnet test --configuration Release --no-build --no-restore
```

For a local demo, generate a high-entropy credential and pass only its SHA-256
hex digest to the API. Keep the original value for the Python agent. This shell
example sets the credential for a disposable local session.

```sh
AGENT_TOKEN="$(python3 -c 'import secrets; print(secrets.token_hex(32))')"
export Telemetry__Agents__demo="$(printf '%s' "$AGENT_TOKEN" | sha256sum | cut -d ' ' -f 1)"
export Telemetry__DatabasePath="$PWD/data/pulse.db"
export ASPNETCORE_ENVIRONMENT=Development
dotnet run --project src/HomeLab.Pulse.Api --no-launch-profile --urls http://127.0.0.1:5080
```

`Telemetry:Agents` maps each approved agent identifier to a unique credential
digest. Load credentials through the runtime configuration provider. The app
reads this registry at startup. Supply the matching raw bearer to the agent
through its existing token file or systemd credential. Agent access is intended
for loopback and the private WireGuard network.

## HTTP contract

`POST /internal/v1/telemetry` uses the existing [v1 schema](../contracts/telemetry-sample.v1.schema.json).
The API embeds that file at build time and validates the entire sample, including
the schema's UUID and date-time formats. Samples may be at most seven days old and
five minutes ahead of the receiver clock. Each request is limited to 64 KiB.

| Status | Meaning |
| --- | --- |
| `204` | The sample was committed, or its authenticated agent and sample ID were already stored. |
| `400` | Invalid JSON, duplicate JSON keys, or a body without a JSON content type. |
| `401` | Missing or invalid bearer credential. |
| `413` | Request body exceeds 64 KiB. |
| `422` | JSON violates the schema or the observation time window. |
| `503` | SQLite could not commit the sample due to an operational failure. The agent can retry. |

`GET /health/live` checks the process, and `GET /health/ready` checks that the
telemetry table is readable. `GET /openapi/v1.json` is available in Development.
The OpenAPI document describes the ingest envelope. The bundled JSON Schema is
the full wire contract.

## Storage and boundaries

One receiver process uses a SQLite file on local storage and applies the EF Core
migration before serving requests. Give the process access to a private state
directory where SQLite can create its WAL and shared-memory files. Startup stops
if the schema cannot be migrated or WAL cannot be enabled. Use the SQLite backup
API or `.backup` for a consistent backup while the receiver is running. Restore
with the receiver stopped, then verify readiness before resuming ingest.

`Telemetry/Ingest` owns authentication, HTTP mapping, schema validation and the
ingest use case. Its persistence boundary saves a sample with one parameterized,
atomic SQLite statement. The primary key is `(AgentId, SampleId)`. Observation
and receipt times are separate UTC tick values. The first accepted payload is
preserved on retry. Health and error responses contain no credentials or payloads.

The xUnit suite uses a temporary SQLite file. It covers duplicate delivery,
restart, request limits, validation, and write-lock recovery, and sends a sample
using the production Python encoder and HTTP transport through Kestrel over loopback.
