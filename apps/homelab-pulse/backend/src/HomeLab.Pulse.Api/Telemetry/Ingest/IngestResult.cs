namespace HomeLab.Pulse.Api.Telemetry.Ingest;

internal enum IngestResult
{
    Accepted,
    InvalidJson,
    InvalidSample,
    StorageUnavailable,
}
