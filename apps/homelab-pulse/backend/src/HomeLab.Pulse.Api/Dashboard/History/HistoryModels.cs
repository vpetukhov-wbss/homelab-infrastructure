namespace HomeLab.Pulse.Api.Dashboard.History;

internal sealed record HistoryWindow(string Name, TimeSpan Duration, TimeSpan Resolution)
{
    public int PointCount => checked((int)(Duration.Ticks / Resolution.Ticks));
}

internal sealed record FleetHistorySnapshot(int SchemaVersion, DateTimeOffset GeneratedAt, FleetHistory History);

internal sealed record FleetHistory(string Window, int ResolutionSeconds, IReadOnlyList<FleetHistoryPoint> Points);

internal sealed record FleetHistoryPoint
{
    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset EndedAt { get; init; }

    public double? CpuUsagePercent { get; init; }

    public long? MemoryUsedBytes { get; init; }

    public long? MemoryTotalBytes { get; init; }

    public required IReadOnlyList<FleetNodePulse> Nodes { get; init; }
}

internal sealed record FleetNodePulse(string Alias, string Status, DateTimeOffset? ObservedAt);

internal sealed record MachineHistory(string Window, int ResolutionSeconds, IReadOnlyList<MachineHistoryPoint> Points);

internal sealed record MachineHistoryPoint
{
    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset EndedAt { get; init; }

    public required string Status { get; init; }

    public DateTimeOffset? ObservedAt { get; init; }

    public double? CpuUsagePercent { get; init; }

    public long? MemoryUsedBytes { get; init; }

    public long? MemoryTotalBytes { get; init; }
}

internal sealed record HistoryProjectionResult(
    IReadOnlyDictionary<string, FleetHistory> Fleet,
    IReadOnlyDictionary<string, MachineHistory> Machines);
