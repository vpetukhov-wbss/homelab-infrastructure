using HomeLab.Pulse.Api.Dashboard.Overview;
using Microsoft.Extensions.Options;

namespace HomeLab.Pulse.Api.Dashboard.Signals;

internal sealed record NodeSignalInput
{
    public required string Alias { get; init; }

    public required string Scope { get; init; }

    public required string Status { get; init; }

    public DateTimeOffset? ObservedAt { get; init; }

    public required OverviewMetrics Metrics { get; init; }

    public required DateTimeOffset EvaluatedAt { get; init; }
}

internal sealed class NodeHealthSignals(IOptions<HealthSignalOptions> configured)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, QualifyingStreak> _streaks = new(StringComparer.Ordinal);
    private readonly HealthSignalOptions _options = configured.Value;

    public IReadOnlyList<HealthSignal> Evaluate(NodeSignalInput input)
    {
        lock (_gate)
        {
            if (input.Status == "stale" && input.ObservedAt.HasValue)
            {
                Clear(input.Scope, input.Alias);
                return [new HealthSignal
                {
                    Severity = "critical",
                    MessageKey = "node.ghosted_us",
                    Window = "90s",
                    Threshold = HostTelemetry.Freshness.TotalSeconds,
                }];
            }

            if (input.Status != "available" || input.ObservedAt is not { } observedAt)
            {
                Clear(input.Scope, input.Alias);
                return [];
            }

            var signals = new List<HealthSignal>();
            AddSustained(signals, new SustainedMetric
            {
                Key = input.Scope + ":" + input.Alias + ":cpu", ObservedAt = observedAt, Value = input.Metrics.CpuUsagePercent,
                Threshold = _options.CpuUsagePercent, MessageKey = "cpu.doing_overtime", Unit = "%",
            });
            AddSustained(signals, new SustainedMetric
            {
                Key = input.Scope + ":" + input.Alias + ":memory", ObservedAt = observedAt,
                Value = Percentage(input.Metrics.MemoryUsedBytes, input.Metrics.MemoryTotalBytes),
                Threshold = _options.MemoryUsagePercent, MessageKey = "memory.getting_crowded", Unit = "%",
            });
            AddSustained(signals, new SustainedMetric
            {
                Key = input.Scope + ":" + input.Alias + ":temperature", ObservedAt = observedAt, Value = input.Metrics.TemperatureCelsius,
                Threshold = _options.TemperatureCelsius, MessageKey = "temperature.getting_hot", Unit = "celsius",
            });

            var diskUsage = Percentage(
                input.Metrics.DiskTotalBytes - input.Metrics.DiskAvailableBytes,
                input.Metrics.DiskTotalBytes);
            if (diskUsage >= _options.DiskUsagePercent)
            {
                signals.Add(new HealthSignal
                {
                    Severity = "warning",
                    MessageKey = "disk.getting_crowded",
                    Value = diskUsage,
                    Unit = "%",
                    Window = "current",
                    Threshold = _options.DiskUsagePercent,
                });
            }

            return signals;
        }
    }

    private void AddSustained(List<HealthSignal> signals, SustainedMetric metric)
    {
        if (metric.Value is not { } measured || measured < metric.Threshold)
        {
            _streaks.Remove(metric.Key);
            return;
        }

        if (!_streaks.TryGetValue(metric.Key, out var streak))
        {
            streak = new QualifyingStreak(metric.ObservedAt, metric.ObservedAt);
            _streaks[metric.Key] = streak;
        }
        else if (metric.ObservedAt > streak.LastObservedAt)
        {
            streak = metric.ObservedAt - streak.LastObservedAt >= HostTelemetry.Freshness
                ? new QualifyingStreak(metric.ObservedAt, metric.ObservedAt)
                : streak with { LastObservedAt = metric.ObservedAt };
            _streaks[metric.Key] = streak;
        }

        if (streak.LastObservedAt - streak.FirstObservedAt >= _options.SustainedWindow)
        {
            signals.Add(new HealthSignal
            {
                Severity = "warning",
                MessageKey = metric.MessageKey,
                Value = metric.Value,
                Unit = metric.Unit,
                Window = Duration(_options.SustainedWindow),
                Threshold = metric.Threshold,
            });
        }
    }

    private void Clear(string scope, string alias)
    {
        var prefix = scope + ":" + alias + ":";
        foreach (var key in _streaks.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
        {
            _streaks.Remove(key);
        }
    }

    private static double? Percentage(long? used, long? total) => total > 0 && used >= 0 && used <= total
        ? used * 100.0 / total : null;

    private static string Duration(TimeSpan value) => value.TotalMinutes >= 1
        ? $"{value.TotalMinutes:0.#}m" : $"{value.TotalSeconds:0.#}s";

    private sealed record QualifyingStreak(DateTimeOffset FirstObservedAt, DateTimeOffset LastObservedAt);

    private sealed record SustainedMetric
    {
        public required string Key { get; init; }

        public required DateTimeOffset ObservedAt { get; init; }

        public double? Value { get; init; }

        public required double Threshold { get; init; }

        public required string MessageKey { get; init; }

        public required string Unit { get; init; }
    }
}
