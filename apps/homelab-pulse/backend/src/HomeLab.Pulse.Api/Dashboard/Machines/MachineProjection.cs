using System.Text.Json;
using HomeLab.Pulse.Api.Dashboard.Overview;
using HomeLab.Pulse.Api.Telemetry.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HomeLab.Pulse.Api.Dashboard.Machines;

internal sealed class MachineProjection(TelemetryDbContext database, MachineCatalog catalog, SmartProjection smart)
{
    public async Task<List<MachineDetails>> ReadAsync(DateTimeOffset now, CancellationToken cancellationToken)
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
            var host = new HostReadouts(status, observedAt, metrics,
                RateStatus(status, approved.Node.Metrics.HasFlag(PublicMetric.Cpu), metrics.CpuUsagePercent.HasValue),
                RateStatus(status, approved.Node.Metrics.HasFlag(PublicMetric.Network),
                    metrics.NetworkRxBytesPerSecond.HasValue || metrics.NetworkTxBytesPerSecond.HasValue),
                status == "available" && approved.IncludeUptime ? HostTelemetry.Number(values, "uptimeSeconds") : null,
                status == "available" && approved.IncludeLoad ? ReadLoad(values) : null);

            machines.Add(new MachineDetails(approved.Node.Alias, approved.Hardware, host,
                ReadFilesystems(approved.Filesystems, values, observedAt, status),
                ReadTemperatures(approved.Temperatures, values, observedAt, status),
                ReadFans(approved.Fans, values, observedAt, status),
                approved.Disks.Select(disk => new DiskReadout(disk.Alias, approved.DiskHardware.GetValueOrDefault(disk.Source),
                    readings.GetValueOrDefault(disk.Source) ?? new SmartReadout("unavailable"))).ToArray()));
        }

        return machines;
    }

    private static FilesystemReadout[] ReadFilesystems(
        IReadOnlyList<MachineSource> sources, JsonElement values, DateTimeOffset? at, string status)
    {
        var filesystems = HostTelemetry.Property(values, "filesystems");
        return sources.Select(source =>
        {
            var reading = HostTelemetry.Property(filesystems, source.Source);
            var total = HostTelemetry.Bytes(reading, "totalBytes");
            var available = HostTelemetry.Bytes(reading, "availableBytes");
            var state = total.HasValue && available.HasValue && available <= total ? status : "unavailable";
            return new FilesystemReadout(source.Alias, state, Timestamp(state, at),
                state == "available" ? total : null, state == "available" ? available : null);
        }).ToArray();
    }

    private static TemperatureReadout[] ReadTemperatures(
        IReadOnlyList<MachineSource> sources, JsonElement values, DateTimeOffset? at, string status)
    {
        var readings = HostTelemetry.Property(values, "temperaturesCelsius");
        return sources.Select(source =>
        {
            var celsius = HostTelemetry.Number(readings, source.Source);
            var state = celsius.HasValue ? status : "unavailable";
            return new TemperatureReadout(source.Alias, state, Timestamp(state, at), state == "available" ? celsius : null);
        }).ToArray();
    }

    private static FanReadout[] ReadFans(
        IReadOnlyList<MachineSource> sources, JsonElement values, DateTimeOffset? at, string status)
    {
        var readings = HostTelemetry.Property(values, "fanSpeedsRpm");
        return sources.Select(source =>
        {
            var rpm = HostTelemetry.Bytes(readings, source.Source);
            var state = rpm.HasValue ? status : "unavailable";
            return new FanReadout(source.Alias, state, Timestamp(state, at), state == "available" ? rpm : null);
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

    private static string RateStatus(string host, bool selected, bool present) =>
        !selected ? "unavailable" : host != "available" ? host : present ? "available" : "warm";

    private static DateTimeOffset? Timestamp(string status, DateTimeOffset? at) => status == "unavailable" ? null : at;
}
