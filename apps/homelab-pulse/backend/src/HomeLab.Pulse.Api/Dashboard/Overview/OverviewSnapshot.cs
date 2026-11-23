using HomeLab.Pulse.Api.Dashboard.Signals;

namespace HomeLab.Pulse.Api.Dashboard.Overview;

internal sealed record OverviewSnapshot
{
    public required int SchemaVersion { get; init; }

    public required long Revision { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    public required IReadOnlyList<OverviewNode> Nodes { get; init; }
}

internal sealed record OverviewNode
{
    public required string Alias { get; init; }

    public required string Status { get; init; }

    public DateTimeOffset? ObservedAt { get; init; }

    public required OverviewMetrics Metrics { get; init; }

    public required IReadOnlyList<HealthSignal> Signals { get; init; }
}

internal sealed record OverviewMetrics
{
    public double? CpuUsagePercent { get; init; }

    public long? MemoryUsedBytes { get; init; }

    public long? MemoryTotalBytes { get; init; }

    public long? SwapUsedBytes { get; init; }

    public long? SwapTotalBytes { get; init; }

    public long? DiskAvailableBytes { get; init; }

    public long? DiskTotalBytes { get; init; }

    public double? NetworkRxBytesPerSecond { get; init; }

    public double? NetworkTxBytesPerSecond { get; init; }

    public double? TemperatureCelsius { get; init; }
}
