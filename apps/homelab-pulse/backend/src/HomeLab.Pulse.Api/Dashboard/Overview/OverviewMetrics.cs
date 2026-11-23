namespace HomeLab.Pulse.Api.Dashboard.Overview;

public sealed record OverviewMetrics
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
