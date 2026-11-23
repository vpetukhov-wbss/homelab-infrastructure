using HomeLab.Pulse.Api.Dashboard.Overview;

namespace HomeLab.Pulse.Api.Dashboard.Machines;

internal sealed record MachineSnapshot(int SchemaVersion, DateTimeOffset GeneratedAt, MachineDetails Machine);

internal sealed record MachineDetails(
    string Alias, HardwareProfile? Hardware, HostReadouts Host, IReadOnlyList<FilesystemReadout> Filesystems,
    IReadOnlyList<TemperatureReadout> Temperatures, IReadOnlyList<FanReadout> Fans, IReadOnlyList<DiskReadout> Disks);

internal sealed record HardwareProfile(
    string? Manufacturer, string? Model, HardwareCpu Cpu, long OsVisibleMemoryBytes, IReadOnlyList<MemoryModule>? MemoryModules);

internal sealed record HardwareCpu(string Architecture, string? Model, long? PhysicalCoreCount, long LogicalCpuCount);

internal sealed record MemoryModule(
    long CapacityBytes, string? Type, long? RatedSpeedMtPerSecond, long? ConfiguredSpeedMtPerSecond);

internal sealed record DiskHardware(string Status, long? CapacityBytes = null, string? Model = null, bool? Rotational = null);

internal sealed record HostReadouts(
    string Status, DateTimeOffset? ObservedAt, OverviewMetrics Metrics, string CpuRateStatus, string NetworkRateStatus,
    double? UptimeSeconds, LoadAverages? LoadAverage);

internal sealed record LoadAverages(double OneMinute, double FiveMinutes, double FifteenMinutes);

internal sealed record FilesystemReadout(string Alias, string Status, DateTimeOffset? ObservedAt, long? TotalBytes, long? AvailableBytes);

internal sealed record TemperatureReadout(string Alias, string Status, DateTimeOffset? ObservedAt, double? Celsius);

internal sealed record FanReadout(string Alias, string Status, DateTimeOffset? ObservedAt, long? Rpm);

internal sealed record DiskReadout(string Alias, DiskHardware? Hardware, SmartReadout Smart);

internal sealed record SmartReadout(
    string Status, DateTimeOffset? ObservedAt = null, long? TemperatureCelsius = null, bool? HealthPassed = null,
    long? NvmePercentageUsed = null, long? NvmeMediaErrors = null, long? ReallocatedSectors = null,
    long? PendingSectors = null, long? OfflineUncorrectableSectors = null);
