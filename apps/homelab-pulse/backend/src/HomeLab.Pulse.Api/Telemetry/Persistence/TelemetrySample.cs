namespace HomeLab.Pulse.Api.Telemetry.Persistence;

internal sealed record TelemetrySample(
    string AgentId,
    Guid SampleId,
    long ObservedAtUtcTicks,
    long ReceivedAtUtcTicks,
    string Payload);
