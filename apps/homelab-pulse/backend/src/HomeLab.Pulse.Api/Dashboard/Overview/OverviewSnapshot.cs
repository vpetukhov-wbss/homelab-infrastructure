namespace HomeLab.Pulse.Api.Dashboard.Overview;

internal sealed record OverviewSnapshot(
    int SchemaVersion, long Revision, DateTimeOffset GeneratedAt, IReadOnlyList<OverviewNode> Nodes);

internal sealed record OverviewNode(
    string Alias, string Status, DateTimeOffset? ObservedAt, OverviewMetrics Metrics);

internal sealed record OverviewMetrics(
    double? CpuUsagePercent = null,
    long? MemoryUsedBytes = null,
    long? MemoryTotalBytes = null,
    long? SwapUsedBytes = null,
    long? SwapTotalBytes = null,
    long? DiskAvailableBytes = null,
    long? DiskTotalBytes = null,
    double? NetworkRxBytesPerSecond = null,
    double? NetworkTxBytesPerSecond = null,
    double? TemperatureCelsius = null);
