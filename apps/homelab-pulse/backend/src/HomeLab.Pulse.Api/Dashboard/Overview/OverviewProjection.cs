using System.Text.Json;
using HomeLab.Pulse.Api.Dashboard.Signals;
using HomeLab.Pulse.Api.Telemetry.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HomeLab.Pulse.Api.Dashboard.Overview;

internal sealed class OverviewProjection(
    TelemetryDbContext database,
    PublicCatalog catalog,
    NodeHealthSignals healthSignals)
{
    public async Task<List<OverviewNode>> ReadAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var nodes = new List<OverviewNode>(catalog.Nodes.Count);
        foreach (var node in catalog.Nodes)
        {
            var samples = HostTelemetry.ForAgent(database, node.AgentId, now);
            var current = await HostTelemetry.NewestFirst(samples).FirstOrDefaultAsync(cancellationToken);
            if (current is null)
            {
                nodes.Add(CreateNode(new NodeSignalInput
                {
                    Alias = node.Alias,
                    Scope = "overview",
                    Status = "unavailable",
                    ObservedAt = null,
                    Metrics = new OverviewMetrics(),
                    EvaluatedAt = now,
                }));
                continue;
            }

            var observed = HostTelemetry.ObservedAt(current);
            if (now - observed >= HostTelemetry.Freshness)
            {
                nodes.Add(CreateNode(new NodeSignalInput
                {
                    Alias = node.Alias,
                    Scope = "overview",
                    Status = "stale",
                    ObservedAt = observed,
                    Metrics = new OverviewMetrics(),
                    EvaluatedAt = now,
                }));
                continue;
            }

            var previous = HostTelemetry.NeedsPrevious(node) ? await HostTelemetry.PreviousAsync(samples, current, cancellationToken) : null;
            using var currentDocument = JsonDocument.Parse(current.Payload);
            using var previousDocument = previous is null ? null : JsonDocument.Parse(previous.Payload);
            var values = HostTelemetry.Property(currentDocument.RootElement, "values");
            var earlier = previousDocument is null ? default : HostTelemetry.Property(previousDocument.RootElement, "values");
            nodes.Add(CreateNode(new NodeSignalInput
            {
                Alias = node.Alias,
                Scope = "overview",
                Status = "available",
                ObservedAt = observed,
                Metrics = HostTelemetry.ReadMetrics(node, new HostMetricWindow
                {
                    Current = values,
                    Previous = earlier,
                    ElapsedSeconds = HostTelemetry.Elapsed(current, previous),
                }),
                EvaluatedAt = now,
            }));
        }

        return nodes;
    }

    private OverviewNode CreateNode(NodeSignalInput input)
    {
        return new OverviewNode
        {
            Alias = input.Alias,
            Status = input.Status,
            ObservedAt = input.ObservedAt,
            Metrics = input.Metrics,
            Signals = healthSignals.Evaluate(input),
        };
    }
}
