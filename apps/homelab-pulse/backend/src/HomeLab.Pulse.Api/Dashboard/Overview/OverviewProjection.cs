using System.Text.Json;
using HomeLab.Pulse.Api.Telemetry.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HomeLab.Pulse.Api.Dashboard.Overview;

internal sealed class OverviewProjection(TelemetryDbContext database, PublicCatalog catalog)
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
                nodes.Add(new OverviewNode(node.Alias, "unavailable", null, new OverviewMetrics()));
                continue;
            }

            var observed = HostTelemetry.ObservedAt(current);
            if (now - observed >= HostTelemetry.Freshness)
            {
                nodes.Add(new OverviewNode(node.Alias, "stale", observed, new OverviewMetrics()));
                continue;
            }

            var previous = HostTelemetry.NeedsPrevious(node) ? await HostTelemetry.PreviousAsync(samples, current, cancellationToken) : null;
            using var currentDocument = JsonDocument.Parse(current.Payload);
            using var previousDocument = previous is null ? null : JsonDocument.Parse(previous.Payload);
            var values = HostTelemetry.Property(currentDocument.RootElement, "values");
            var earlier = previousDocument is null ? default : HostTelemetry.Property(previousDocument.RootElement, "values");
            nodes.Add(new OverviewNode(node.Alias, "available", observed,
                HostTelemetry.ReadMetrics(node, values, earlier, HostTelemetry.Elapsed(current, previous))));
        }

        return nodes;
    }
}
