using System.Text.Json;
using HomeLab.Pulse.Api.Dashboard.Machines;
using HomeLab.Pulse.Api.Dashboard.Overview;
using HomeLab.Pulse.Api.Telemetry.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HomeLab.Pulse.Api.Dashboard.History;

internal sealed class HistoryProjection(TelemetryDbContext database, MachineCatalog catalog)
{
    internal static readonly IReadOnlyList<HistoryWindow> Windows =
    [
        new("1h", TimeSpan.FromHours(1), TimeSpan.FromMinutes(2)),
        new("24h", TimeSpan.FromHours(24), TimeSpan.FromMinutes(15)),
        new("7d", TimeSpan.FromDays(7), TimeSpan.FromHours(1)),
    ];

    public async Task<HistoryProjectionResult> ReadAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var layouts = Windows.Select(window => new BucketLayout(window, BucketEnd(now, window.Resolution))).ToArray();
        var cpuWeights = catalog.Machines.ToDictionary(
            machine => machine.Node.Alias,
            machine => machine.Hardware?.Cpu.LogicalCpuCount,
            StringComparer.Ordinal);
        var machineSeries = new Dictionary<string, MachineHistoryPoint[][]>(StringComparer.Ordinal);
        foreach (var approved in catalog.Machines)
        {
            machineSeries.Add(approved.Node.Alias, layouts.Select(layout => EmptyPoints(layout)).ToArray());
            for (var index = 0; index < layouts.Length; index++)
            {
                await ReadWindowAsync(
                    approved,
                    new WindowOutput(layouts[index], machineSeries[approved.Node.Alias][index], now),
                    cancellationToken);
            }
        }

        var source = new FleetProjectionSource(machineSeries, cpuWeights);
        var fleet = layouts.Select((layout, index) => new FleetHistory(
            layout.Window.Name,
            checked((int)layout.Window.Resolution.TotalSeconds),
            BuildFleetPoints(layout, index, source))).ToDictionary(item => item.Window, StringComparer.Ordinal);
        var oneHour = layouts[0];
        var machines = machineSeries.ToDictionary(
            item => item.Key,
            item => new MachineHistory(oneHour.Window.Name, checked((int)oneHour.Window.Resolution.TotalSeconds), item.Value[0]),
            StringComparer.Ordinal);
        return new HistoryProjectionResult(fleet, machines);
    }

    private async Task ReadWindowAsync(
        ApprovedMachine approved,
        WindowOutput output,
        CancellationToken cancellationToken)
    {
        var start = output.Layout.Start.UtcTicks;
        var end = output.ObservedThrough.UtcTicks;
        var resolution = output.Layout.Window.Resolution.Ticks;
        var query = database.Samples.FromSqlInterpolated($$"""
            SELECT "AgentId", "SampleId", "ObservedAtUtcTicks", "ReceivedAtUtcTicks", "Payload"
            FROM (
                SELECT "AgentId", "SampleId", "ObservedAtUtcTicks", "ReceivedAtUtcTicks", "Payload",
                    ROW_NUMBER() OVER (
                        PARTITION BY (("ObservedAtUtcTicks" - {{start}}) / {{resolution}})
                        ORDER BY "ObservedAtUtcTicks" DESC, "ReceivedAtUtcTicks" DESC, "SampleId" DESC) AS "HistoryRank"
                FROM "TelemetrySamples"
                WHERE "AgentId" = {{approved.Node.AgentId}}
                    AND "ObservedAtUtcTicks" >= {{start}}
                    AND "ObservedAtUtcTicks" < {{end}}
            ) AS "RankedHistory"
            WHERE "HistoryRank" <= 2
            ORDER BY "ObservedAtUtcTicks", "ReceivedAtUtcTicks", "SampleId"
            """).AsNoTracking().AsAsyncEnumerable();

        TelemetrySample? previous = null;
        TelemetrySample? selected = null;
        var previousBucket = -1;
        await foreach (var sample in query.WithCancellation(cancellationToken))
        {
            if (selected is not null && selected.ObservedAtUtcTicks != sample.ObservedAtUtcTicks)
            {
                var bucket = output.Bucket(selected);
                previous = bucket == previousBucket ? previous : null;
                ProjectSample(approved, output, new SamplePair(selected, previous));
                previous = selected;
                previousBucket = bucket;
            }

            selected = sample;
        }

        if (selected is not null)
        {
            var bucket = output.Bucket(selected);
            previous = bucket == previousBucket ? previous : null;
            ProjectSample(approved, output, new SamplePair(selected, previous));
        }
    }

    private static void ProjectSample(
        ApprovedMachine approved,
        WindowOutput output,
        SamplePair pair)
    {
        using var currentDocument = JsonDocument.Parse(pair.Current.Payload);
        var values = HostTelemetry.Property(currentDocument.RootElement, "values");
        var elapsed = HostTelemetry.Elapsed(pair.Current, pair.Previous);
        var eligiblePrevious = elapsed > 0 && elapsed <= HostTelemetry.Freshness.TotalSeconds ? pair.Previous : null;
        using var previousDocument = eligiblePrevious is null ? null : JsonDocument.Parse(eligiblePrevious.Payload);
        var earlier = previousDocument is null ? default : HostTelemetry.Property(previousDocument.RootElement, "values");
        var metrics = HostTelemetry.ReadMetrics(approved.Node, values, earlier, elapsed);
        var observedAt = HostTelemetry.ObservedAt(pair.Current);
        var bucket = output.Bucket(pair.Current);
        output.Points[bucket] = new MachineHistoryPoint
        {
            StartedAt = output.Points[bucket].StartedAt,
            EndedAt = output.Points[bucket].EndedAt,
            Status = "available",
            ObservedAt = observedAt,
            CpuUsagePercent = metrics.CpuUsagePercent,
            MemoryUsedBytes = metrics.MemoryUsedBytes,
            MemoryTotalBytes = metrics.MemoryTotalBytes,
        };
    }

    private static FleetHistoryPoint[] BuildFleetPoints(
        BucketLayout layout,
        int windowIndex,
        FleetProjectionSource source)
    {
        var points = new FleetHistoryPoint[layout.Window.PointCount];
        for (var bucket = 0; bucket < points.Length; bucket++)
        {
            var nodePoints = source.Machines.Select(machine => (machine.Key, Point: machine.Value[windowIndex][bucket])).ToArray();
            var cpu = WeightedCpu(nodePoints, source.CpuWeights);
            var memory = Memory(nodePoints);
            points[bucket] = new FleetHistoryPoint
            {
                StartedAt = layout.Start + (layout.Window.Resolution * bucket),
                EndedAt = layout.Start + (layout.Window.Resolution * (bucket + 1)),
                CpuUsagePercent = cpu,
                MemoryUsedBytes = memory.Used,
                MemoryTotalBytes = memory.Total,
                Nodes = nodePoints.Select(node => new FleetNodePulse(node.Key, node.Point.Status, node.Point.ObservedAt)).ToArray(),
            };
        }

        return points;
    }

    private static double? WeightedCpu(
        IReadOnlyList<(string Key, MachineHistoryPoint Point)> points,
        IReadOnlyDictionary<string, long?> weights)
    {
        var values = points.Where(item => item.Point.CpuUsagePercent.HasValue && weights[item.Key] > 0).ToArray();
        if (values.Length == 0)
        {
            return null;
        }

        var totalWeight = values.Sum(item => weights[item.Key]!.Value);
        return values.Sum(item => item.Point.CpuUsagePercent!.Value * weights[item.Key]!.Value) / totalWeight;
    }

    private static (long? Used, long? Total) Memory(IReadOnlyList<(string Key, MachineHistoryPoint Point)> points)
    {
        var values = points.Where(item => item.Point.MemoryUsedBytes.HasValue && item.Point.MemoryTotalBytes.HasValue).ToArray();
        return values.Length == 0 ? (null, null) : (values.Sum(item => item.Point.MemoryUsedBytes!.Value), values.Sum(item => item.Point.MemoryTotalBytes!.Value));
    }

    private static MachineHistoryPoint[] EmptyPoints(BucketLayout layout) => Enumerable.Range(0, layout.Window.PointCount)
        .Select(index => new MachineHistoryPoint
        {
            StartedAt = layout.Start + (layout.Window.Resolution * index),
            EndedAt = layout.Start + (layout.Window.Resolution * (index + 1)),
            Status = "missing",
        }).ToArray();

    private static DateTimeOffset BucketEnd(DateTimeOffset now, TimeSpan resolution)
    {
        var endTicks = checked(((now.UtcTicks / resolution.Ticks) + 1) * resolution.Ticks);
        return new DateTimeOffset(endTicks, TimeSpan.Zero);
    }

    private sealed record BucketLayout(HistoryWindow Window, DateTimeOffset End)
    {
        public DateTimeOffset Start => End - Window.Duration;
    }

    private sealed record WindowOutput(
        BucketLayout Layout,
        MachineHistoryPoint[] Points,
        DateTimeOffset ObservedThrough)
    {
        public int Bucket(TelemetrySample sample) =>
            checked((int)((sample.ObservedAtUtcTicks - Layout.Start.UtcTicks) / Layout.Window.Resolution.Ticks));
    }

    private sealed record SamplePair(TelemetrySample Current, TelemetrySample? Previous);

    private sealed record FleetProjectionSource(
        IReadOnlyDictionary<string, MachineHistoryPoint[][]> Machines,
        IReadOnlyDictionary<string, long?> CpuWeights);
}
