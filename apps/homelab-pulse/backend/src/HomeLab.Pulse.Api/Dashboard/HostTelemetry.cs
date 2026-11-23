using System.Text.Json;
using HomeLab.Pulse.Api.Dashboard.Overview;
using HomeLab.Pulse.Api.Telemetry.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HomeLab.Pulse.Api.Dashboard;

internal static class HostTelemetry
{
    internal static readonly TimeSpan Freshness = TimeSpan.FromSeconds(90);
    private static readonly string[] CpuCounters = ["user", "nice", "system", "idle", "iowait", "irq", "softirq", "steal"];

    internal static IQueryable<TelemetrySample> ForAgent(TelemetryDbContext database, string agentId, DateTimeOffset now) =>
        database.Samples.AsNoTracking().Where(item => item.AgentId == agentId && item.ObservedAtUtcTicks <= now.UtcTicks);

    internal static IOrderedQueryable<TelemetrySample> NewestFirst(IQueryable<TelemetrySample> samples) =>
        samples.OrderByDescending(item => item.ObservedAtUtcTicks)
            .ThenByDescending(item => item.ReceivedAtUtcTicks)
            .ThenByDescending(item => item.SampleId);

    internal static Task<TelemetrySample?> PreviousAsync(
        IQueryable<TelemetrySample> samples, TelemetrySample current, CancellationToken cancellationToken) =>
        NewestFirst(samples.Where(item => item.ObservedAtUtcTicks < current.ObservedAtUtcTicks
            && item.ObservedAtUtcTicks >= current.ObservedAtUtcTicks - Freshness.Ticks)).FirstOrDefaultAsync(cancellationToken);

    internal static bool NeedsPrevious(PublicNode node) => (node.Metrics & (PublicMetric.Cpu | PublicMetric.Network)) != 0;

    internal static double Elapsed(TelemetrySample current, TelemetrySample? previous) => previous is null ? 0
        : TimeSpan.FromTicks(current.ObservedAtUtcTicks - previous.ObservedAtUtcTicks).TotalSeconds;

    internal static DateTimeOffset ObservedAt(TelemetrySample sample) => new(sample.ObservedAtUtcTicks, TimeSpan.Zero);

    internal static OverviewMetrics ReadMetrics(PublicNode node, HostMetricWindow window)
    {
        var boot = Property(window.Current, "bootId");
        var sameBoot = boot.ValueKind == JsonValueKind.String && Property(window.Previous, "bootId").ValueKind == JsonValueKind.String
            && string.Equals(boot.GetString(), Property(window.Previous, "bootId").GetString(), StringComparison.Ordinal);
        var memory = Property(window.Current, "memory");
        var ram = node.Metrics.HasFlag(PublicMetric.Memory) ? Usage(memory, "totalBytes", "availableBytes") : default;
        var swap = node.Metrics.HasFlag(PublicMetric.Swap) ? Usage(memory, "swapTotalBytes", "swapFreeBytes") : default;
        var disk = node.Filesystem is { } filesystem
            ? Usage(Property(Property(window.Current, "filesystems"), filesystem), "totalBytes", "availableBytes") : default;
        var currentNetwork = node.NetworkInterface is { } network ? Property(Property(window.Current, "network"), network) : default;
        var pastNetwork = node.NetworkInterface is { } pastInterface ? Property(Property(window.Previous, "network"), pastInterface) : default;
        var networkWindow = new CounterWindow(currentNetwork, pastNetwork, window.ElapsedSeconds);
        return new OverviewMetrics
        {
            CpuUsagePercent = sameBoot && node.Metrics.HasFlag(PublicMetric.Cpu) ? CpuUsage(window.Current, window.Previous) : null,
            MemoryUsedBytes = ram.Used,
            MemoryTotalBytes = ram.Total,
            SwapUsedBytes = swap.Used,
            SwapTotalBytes = swap.Total,
            DiskAvailableBytes = disk.Total - disk.Used,
            DiskTotalBytes = disk.Total,
            NetworkRxBytesPerSecond = sameBoot ? Rate(networkWindow, "rxBytes") : null,
            NetworkTxBytesPerSecond = sameBoot ? Rate(networkWindow, "txBytes") : null,
            TemperatureCelsius = node.TemperatureSensor is { } sensor
                ? Number(Property(window.Current, "temperaturesCelsius"), sensor) : null,
        };
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

    private static double? Rate(CounterWindow window, string key) =>
        window.ElapsedSeconds > 0 && Bytes(window.Previous, key) is { } before
            && Bytes(window.Current, key) is { } after && after >= before
            ? (after - before) / window.ElapsedSeconds : null;

    private static (long? Total, long? Used) Usage(JsonElement source, string totalKey, string availableKey) =>
        Bytes(source, totalKey) is { } total && Bytes(source, availableKey) is { } available && available <= total
            ? (total, total - available) : (null, null);

    internal static long? Bytes(JsonElement source, string key)
    {
        var element = Property(source, key);
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var number) && number >= 0
            ? number : null;
    }

    internal static double? Number(JsonElement source, string key)
    {
        var element = Property(source, key);
        return element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number) && double.IsFinite(number)
            ? number : null;
    }

    internal static JsonElement Property(JsonElement source, string key) =>
        source.ValueKind == JsonValueKind.Object && source.TryGetProperty(key, out var property) ? property : default;

    private sealed record CounterWindow(JsonElement Current, JsonElement Previous, double ElapsedSeconds);
}

internal sealed record HostMetricWindow
{
    public required JsonElement Current { get; init; }

    public required JsonElement Previous { get; init; }

    public required double ElapsedSeconds { get; init; }
}
