using HomeLab.Pulse.Api.Dashboard.Signals;

namespace HomeLab.Pulse.Api.Dashboard.Overview;

public sealed record OverviewNode
{
    public required string Alias { get; init; }

    public required string Status { get; init; }

    public DateTimeOffset? ObservedAt { get; init; }

    public required OverviewMetrics Metrics { get; init; }

    public required IReadOnlyList<HealthSignal> Signals { get; init; }
}
