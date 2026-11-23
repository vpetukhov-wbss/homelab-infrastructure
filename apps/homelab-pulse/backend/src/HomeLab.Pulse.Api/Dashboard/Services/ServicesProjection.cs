using System.Text.Json;

namespace HomeLab.Pulse.Api.Dashboard.Services;

internal sealed class ServicesProjection(ServiceCatalog catalog, ServiceObservations observations)
{
    internal static readonly TimeSpan Freshness = TimeSpan.FromSeconds(90);

    public async Task<List<NodeServices>> ReadAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var nodes = new List<NodeServices>(catalog.Nodes.Count);
        foreach (var node in catalog.Nodes)
        {
            var observed = await observations.ReadAsync(node, now, cancellationToken);
            nodes.Add(new NodeServices(node.Alias,
                Project(node, observed, ServiceKind.PostgreSql, now, ServiceMetrics.PostgreSql),
                Project(node, observed, ServiceKind.Redis, now, ServiceMetrics.Redis),
                Project(node, observed, ServiceKind.RabbitMq, now, (latest, _, _) => ServiceMetrics.RabbitMq(latest)),
                Project(node, observed, ServiceKind.Sqlite, now, (latest, _, _) => ServiceMetrics.Sqlite(latest))));
        }

        return nodes;
    }

    private static ServiceReading<T>[] Project<T>(
        ServiceNode node, Dictionary<string, ServiceObservationPair> observed, ServiceKind kind, DateTimeOffset now,
        Func<JsonElement, JsonElement, double?, T> metrics) where T : class =>
        node.Services.Where(service => service.Kind == kind).Select(service =>
        {
            if (!observed.TryGetValue(service.Alias, out var pair))
            {
                return new ServiceReading<T>(service.Alias, "unknown", null, null);
            }

            var latest = pair.Latest;
            if (now - latest.ObservedAt >= Freshness)
            {
                return new ServiceReading<T>(service.Alias, "stale", latest.ObservedAt, null);
            }

            if (!latest.Available)
            {
                return new ServiceReading<T>(service.Alias, "unavailable", latest.ObservedAt, null);
            }

            var previous = pair.Previous;
            var window = latest.ObservedAt - previous?.ObservedAt;
            var elapsed = previous is { Available: true } && previous.BootId == latest.BootId
                && window > TimeSpan.Zero && window <= Freshness ? window.Value.TotalSeconds : (double?)null;
            return new ServiceReading<T>(service.Alias, "available", latest.ObservedAt,
                metrics(latest.Data, elapsed.HasValue ? previous!.Data : default, elapsed));
        }).ToArray();
}
