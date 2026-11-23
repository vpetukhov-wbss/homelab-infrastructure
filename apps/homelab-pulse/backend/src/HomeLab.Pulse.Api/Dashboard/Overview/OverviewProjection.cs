using System.Text.Json;
using HomeLab.Pulse.Api.Telemetry.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HomeLab.Pulse.Api.Dashboard.Overview;

internal sealed class OverviewProjection(TelemetryDbContext database, PublicCatalog catalog)
{
    internal static readonly TimeSpan Freshness = TimeSpan.FromSeconds(90);
    private static readonly string[] CpuCounters = ["user", "nice", "system", "idle", "iowait", "irq", "softirq", "steal"];

    public async Task<List<OverviewNode>> ReadAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var nodes = new List<OverviewNode>(catalog.Nodes.Count);
        foreach (var node in catalog.Nodes)
        {
            var samples = database.Samples.AsNoTracking()
                .Where(item => item.AgentId == node.AgentId && item.ObservedAtUtcTicks <= now.UtcTicks);
            var current = await LatestAsync(samples, cancellationToken);
            if (current is null)
            {
                nodes.Add(new OverviewNode(node.Alias, "unavailable", null, new OverviewMetrics()));
                continue;
            }

            var observed = new DateTimeOffset(current.ObservedAtUtcTicks, TimeSpan.Zero);
            if (now - observed >= Freshness)
            {
                nodes.Add(new OverviewNode(node.Alias, "stale", observed, new OverviewMetrics()));
                continue;
            }

            TelemetrySample? previous = null;
            if ((node.Metrics & (PublicMetric.Cpu | PublicMetric.Network)) != 0)
            {
                previous = await LatestAsync(samples.Where(item => item.ObservedAtUtcTicks < current.ObservedAtUtcTicks
                    && item.ObservedAtUtcTicks >= current.ObservedAtUtcTicks - Freshness.Ticks), cancellationToken);
            }

            nodes.Add(new OverviewNode(node.Alias, "available", observed, ReadMetrics(node, current, previous)));
        }

        return nodes;
    }

    private static Task<TelemetrySample?> LatestAsync(IQueryable<TelemetrySample> samples, CancellationToken cancellationToken) =>
        samples.OrderByDescending(item => item.ObservedAtUtcTicks)
            .ThenByDescending(item => item.ReceivedAtUtcTicks)
            .ThenByDescending(item => item.SampleId)
            .FirstOrDefaultAsync(cancellationToken);

    private static OverviewMetrics ReadMetrics(PublicNode node, TelemetrySample current, TelemetrySample? previous)
    {
        using var currentDocument = JsonDocument.Parse(current.Payload);
        using var previousDocument = previous is null ? null : JsonDocument.Parse(previous.Payload);
        var values = Property(currentDocument.RootElement, "values");
        var earlier = previousDocument is null ? default : Property(previousDocument.RootElement, "values");
        var boot = Property(values, "bootId");
        var sameBoot = boot.ValueKind == JsonValueKind.String && Property(earlier, "bootId").ValueKind == JsonValueKind.String
            && string.Equals(boot.GetString(), Property(earlier, "bootId").GetString(), StringComparison.Ordinal);
        var memory = Property(values, "memory");
        var ram = node.Metrics.HasFlag(PublicMetric.Memory) ? Usage(memory, "totalBytes", "availableBytes") : default;
        var swap = node.Metrics.HasFlag(PublicMetric.Swap) ? Usage(memory, "swapTotalBytes", "swapFreeBytes") : default;
        var disk = node.Filesystem is { } filesystem
            ? Usage(Property(Property(values, "filesystems"), filesystem), "totalBytes", "availableBytes") : default;
        var currentNetwork = node.NetworkInterface is { } network ? Property(Property(values, "network"), network) : default;
        var pastNetwork = node.NetworkInterface is { } pastInterface ? Property(Property(earlier, "network"), pastInterface) : default;
        var elapsed = previous is null ? 0 : TimeSpan.FromTicks(current.ObservedAtUtcTicks - previous.ObservedAtUtcTicks).TotalSeconds;
        return new OverviewMetrics(
            CpuUsagePercent: sameBoot && node.Metrics.HasFlag(PublicMetric.Cpu) ? CpuUsage(values, earlier) : null,
            MemoryUsedBytes: ram.Used, MemoryTotalBytes: ram.Total,
            SwapUsedBytes: swap.Used, SwapTotalBytes: swap.Total,
            DiskAvailableBytes: disk.Total - disk.Used, DiskTotalBytes: disk.Total,
            NetworkRxBytesPerSecond: sameBoot ? Rate(currentNetwork, pastNetwork, "rxBytes", elapsed) : null,
            NetworkTxBytesPerSecond: sameBoot ? Rate(currentNetwork, pastNetwork, "txBytes", elapsed) : null,
            TemperatureCelsius: node.TemperatureSensor is { } sensor
                ? Number(Property(values, "temperaturesCelsius"), sensor) : null);
    }

    private static double? CpuUsage(JsonElement current, JsonElement previous)
    {
        var first = Property(previous, "cpuTimesSeconds");
        var second = Property(current, "cpuTimesSeconds");
        var total = 0.0;
        var busy = 0.0;
        foreach (var key in CpuCounters)
        {
            if (Number(first, key) is not { } before || Number(second, key) is not { } after || after < before)
            {
                return null;
            }

            var delta = after - before;
            total += delta;
            if (key is not ("idle" or "iowait"))
            {
                busy += delta;
            }
        }

        return total > 0 && double.IsFinite(total) ? 100 * (busy / total) : null;
    }

    private static double? Rate(JsonElement current, JsonElement previous, string key, double elapsed) =>
        elapsed > 0 && Bytes(previous, key) is { } before && Bytes(current, key) is { } after && after >= before
            ? (after - before) / elapsed : null;

    private static (long? Total, long? Used) Usage(JsonElement source, string totalKey, string availableKey) =>
        Bytes(source, totalKey) is { } total && Bytes(source, availableKey) is { } available && available <= total
            ? (total, total - available) : (null, null);

    private static long? Bytes(JsonElement source, string key)
    {
        var element = Property(source, key);
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number) && number >= 0
            ? number : null;
    }

    private static double? Number(JsonElement source, string key)
    {
        var element = Property(source, key);
        return element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number) && double.IsFinite(number)
            ? number : null;
    }

    private static JsonElement Property(JsonElement source, string key) =>
        source.ValueKind == JsonValueKind.Object && source.TryGetProperty(key, out var property) ? property : default;
}
