using System.Text.Json;
using HomeLab.Pulse.Api.Dashboard.Overview;
using HomeLab.Pulse.Api.Dashboard.History;
using HomeLab.Pulse.Api.Telemetry.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HomeLab.Pulse.Api.Dashboard.Machines;

internal sealed class MachineProjection(TelemetryDbContext database, MachineCatalog catalog, SmartProjection smart)
{
    public async Task<List<MachineDetails>> ReadAsync(
        DateTimeOffset now,
        HistoryPublication history,
        CancellationToken cancellationToken)
    {
        var machines = new List<MachineDetails>(catalog.Machines.Count);
        foreach (var approved in catalog.Machines)
        {
            var samples = HostTelemetry.ForAgent(database, approved.Node.AgentId, now);
            var current = await HostTelemetry.NewestFirst(samples).FirstOrDefaultAsync(cancellationToken);
            var observedAt = current is null ? (DateTimeOffset?)null : HostTelemetry.ObservedAt(current);
            var status = current is null ? "unavailable" : now - observedAt >= HostTelemetry.Freshness ? "stale" : "available";
            var readings = await smart.ReadAsync(approved, now, cancellationToken);

            using var document = current is null ? null : JsonDocument.Parse(current.Payload);
            var values = document is null ? default : HostTelemetry.Property(document.RootElement, "values");
            var previous = status == "available" && HostTelemetry.NeedsPrevious(approved.Node)
                ? await HostTelemetry.PreviousAsync(samples, current!, cancellationToken) : null;
            using var earlier = previous is null ? null : JsonDocument.Parse(previous.Payload);
            var earlierValues = earlier is null ? default : HostTelemetry.Property(earlier.RootElement, "values");
            var metrics = status != "available" ? new OverviewMetrics()
                : HostTelemetry.ReadMetrics(approved.Node, values, earlierValues, HostTelemetry.Elapsed(current!, previous));
            var observation = new MachineObservation(values, observedAt, status);
            var host = new HostReadouts
            {
                Status = status,
                ObservedAt = observedAt,
                Metrics = metrics,
                CpuRateStatus = RateStatus(hostStatus: status,
                    selected: approved.Node.Metrics.HasFlag(PublicMetric.Cpu), present: metrics.CpuUsagePercent.HasValue),
                NetworkRateStatus = RateStatus(hostStatus: status,
                    selected: approved.Node.Metrics.HasFlag(PublicMetric.Network),
                    present: metrics.NetworkRxBytesPerSecond.HasValue || metrics.NetworkTxBytesPerSecond.HasValue),
                UptimeSeconds = status == "available" && approved.IncludeUptime ? HostTelemetry.Number(values, "uptimeSeconds") : null,
                LoadAverage = status == "available" && approved.IncludeLoad ? ReadLoad(values) : null,
            };

            machines.Add(new MachineDetails
            {
                Alias = approved.Node.Alias,
                Hardware = approved.Hardware,
                Host = host,
                Filesystems = ReadFilesystems(approved.Filesystems, observation),
                Temperatures = ReadTemperatures(approved.Temperatures, observation),
                Fans = ReadFans(approved.Fans, observation),
                Disks = approved.Disks.Select(disk => new DiskReadout(disk.Alias, approved.DiskHardware.GetValueOrDefault(disk.Source),
                    readings.GetValueOrDefault(disk.Source) ?? new SmartReadout { Status = "unavailable" })).ToArray(),
                History = history.FindMachine(approved.Node.Alias) ?? EmptyHistory(),
            });
        }

        return machines;
    }

    private static FilesystemReadout[] ReadFilesystems(
        IReadOnlyList<MachineSource> sources,
        MachineObservation observation)
    {
        var filesystems = HostTelemetry.Property(observation.Values, "filesystems");
        return sources.Select(source =>
        {
            var reading = HostTelemetry.Property(filesystems, source.Source);
            var total = HostTelemetry.Bytes(reading, "totalBytes");
            var available = HostTelemetry.Bytes(reading, "availableBytes");
            var state = total.HasValue && available.HasValue && available <= total ? observation.Status : "unavailable";
            return new FilesystemReadout
            {
                Alias = source.Alias,
                Status = state,
                ObservedAt = Timestamp(state, observation.ObservedAt),
                TotalBytes = state == "available" ? total : null,
                AvailableBytes = state == "available" ? available : null,
            };
        }).ToArray();
    }

    private static TemperatureReadout[] ReadTemperatures(
        IReadOnlyList<MachineSource> sources,
        MachineObservation observation)
    {
        var readings = HostTelemetry.Property(observation.Values, "temperaturesCelsius");
        return sources.Select(source =>
        {
            var celsius = HostTelemetry.Number(readings, source.Source);
            var state = celsius.HasValue ? observation.Status : "unavailable";
            return new TemperatureReadout
            {
                Alias = source.Alias,
                Status = state,
                ObservedAt = Timestamp(state, observation.ObservedAt),
                Celsius = state == "available" ? celsius : null,
            };
        }).ToArray();
    }

    private static FanReadout[] ReadFans(
        IReadOnlyList<MachineSource> sources,
        MachineObservation observation)
    {
        var readings = HostTelemetry.Property(observation.Values, "fanSpeedsRpm");
        return sources.Select(source =>
        {
            var rpm = HostTelemetry.Bytes(readings, source.Source);
            var state = rpm.HasValue ? observation.Status : "unavailable";
            return new FanReadout
            {
                Alias = source.Alias,
                Status = state,
                ObservedAt = Timestamp(state, observation.ObservedAt),
                Rpm = state == "available" ? rpm : null,
            };
        }).ToArray();
    }

    private static LoadAverages? ReadLoad(JsonElement values)
    {
        var load = HostTelemetry.Property(values, "loadAverage");
        return load.ValueKind == JsonValueKind.Array && load.GetArrayLength() == 3
            && load[0].TryGetDouble(out var one) && double.IsFinite(one)
            && load[1].TryGetDouble(out var five) && double.IsFinite(five)
            && load[2].TryGetDouble(out var fifteen) && double.IsFinite(fifteen)
            ? new LoadAverages(one, five, fifteen) : null;
    }

    private static string RateStatus(string hostStatus, bool selected, bool present) =>
        !selected ? "unavailable" : hostStatus != "available" ? hostStatus : present ? "available" : "warm";

    private static DateTimeOffset? Timestamp(string status, DateTimeOffset? at) => status == "unavailable" ? null : at;

    private static MachineHistory EmptyHistory() => new("1h", 120, []);

    private sealed record MachineObservation(JsonElement Values, DateTimeOffset? ObservedAt, string Status);
}
