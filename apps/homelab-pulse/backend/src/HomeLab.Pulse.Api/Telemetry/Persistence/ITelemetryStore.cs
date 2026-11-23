namespace HomeLab.Pulse.Api.Telemetry.Persistence;

internal interface ITelemetryStore
{
    Task<bool> TrySaveAsync(TelemetrySample sample, CancellationToken cancellationToken);
}
