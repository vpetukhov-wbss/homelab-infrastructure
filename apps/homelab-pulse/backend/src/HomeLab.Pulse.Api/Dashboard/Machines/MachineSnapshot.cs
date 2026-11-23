using HomeLab.Pulse.Api.Dashboard.Overview;
using HomeLab.Pulse.Api.Dashboard.History;

namespace HomeLab.Pulse.Api.Dashboard.Machines;

internal sealed record MachineSnapshot(int SchemaVersion, DateTimeOffset GeneratedAt, MachineDetails Machine);

internal sealed record MachineDetails
{
    public required string Alias { get; init; }

    public HardwareProfile? Hardware { get; init; }

    public required HostReadouts Host { get; init; }

    public required IReadOnlyList<FilesystemReadout> Filesystems { get; init; }

    public required IReadOnlyList<TemperatureReadout> Temperatures { get; init; }

    public required IReadOnlyList<FanReadout> Fans { get; init; }

    public required IReadOnlyList<DiskReadout> Disks { get; init; }

    public required MachineHistory History { get; init; }
}

internal sealed record HardwareProfile
{
    public string? Manufacturer { get; init; }

    public string? Model { get; init; }

    public required HardwareCpu Cpu { get; init; }

    public required long OsVisibleMemoryBytes { get; init; }

    public IReadOnlyList<MemoryModule>? MemoryModules { get; init; }
}

internal sealed record HardwareCpu
{
    public required string Architecture { get; init; }

    public string? Model { get; init; }

    public long? PhysicalCoreCount { get; init; }

    public required long LogicalCpuCount { get; init; }
}

internal sealed record MemoryModule
{
    public required long CapacityBytes { get; init; }

    public string? Type { get; init; }

    public long? RatedSpeedMtPerSecond { get; init; }

    public long? ConfiguredSpeedMtPerSecond { get; init; }
}

internal sealed record DiskHardware
{
    public required string Status { get; init; }

    public long? CapacityBytes { get; init; }

    public string? Model { get; init; }

    public bool? Rotational { get; init; }
}

internal sealed record HostReadouts
{
    public required string Status { get; init; }

    public DateTimeOffset? ObservedAt { get; init; }

    public required OverviewMetrics Metrics { get; init; }

    public required string CpuRateStatus { get; init; }

    public required string NetworkRateStatus { get; init; }

    public double? UptimeSeconds { get; init; }

    public LoadAverages? LoadAverage { get; init; }
}

internal sealed record LoadAverages(double OneMinute, double FiveMinutes, double FifteenMinutes);

internal sealed record FilesystemReadout
{
    public required string Alias { get; init; }

    public required string Status { get; init; }

    public DateTimeOffset? ObservedAt { get; init; }

    public long? TotalBytes { get; init; }

    public long? AvailableBytes { get; init; }
}

internal sealed record TemperatureReadout
{
    public required string Alias { get; init; }

    public required string Status { get; init; }

    public DateTimeOffset? ObservedAt { get; init; }

    public double? Celsius { get; init; }
}

internal sealed record FanReadout
{
    public required string Alias { get; init; }

    public required string Status { get; init; }

    public DateTimeOffset? ObservedAt { get; init; }

    public long? Rpm { get; init; }
}

internal sealed record DiskReadout(string Alias, DiskHardware? Hardware, SmartReadout Smart);

internal sealed record SmartReadout
{
    public required string Status { get; init; }

    public DateTimeOffset? ObservedAt { get; init; }

    public long? TemperatureCelsius { get; init; }

    public bool? HealthPassed { get; init; }

    public long? NvmePercentageUsed { get; init; }

    public long? NvmeMediaErrors { get; init; }

    public long? ReallocatedSectors { get; init; }

    public long? PendingSectors { get; init; }

    public long? OfflineUncorrectableSectors { get; init; }
}
