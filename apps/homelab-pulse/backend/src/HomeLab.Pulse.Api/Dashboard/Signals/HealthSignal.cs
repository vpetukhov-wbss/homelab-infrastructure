namespace HomeLab.Pulse.Api.Dashboard.Signals;

public sealed record HealthSignal
{
    public required string Severity { get; init; }

    public required string MessageKey { get; init; }

    public double? Value { get; init; }

    public string? Unit { get; init; }

    public string? Window { get; init; }

    public double? Threshold { get; init; }
}
