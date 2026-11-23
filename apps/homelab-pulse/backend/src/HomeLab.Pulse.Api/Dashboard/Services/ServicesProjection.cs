using System.Text.Json;

namespace HomeLab.Pulse.Api.Dashboard.Services;

internal sealed record ServicesProjectionResult(IReadOnlyList<NodeServices> Nodes, DateTimeOffset? FreshUntil);

internal sealed class ServicesProjection(ServiceCatalog catalog, ServiceObservations observations)
{
    private static readonly TimeSpan ProbeFreshness = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan SnapshotFreshness = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan VolumeFreshness = TimeSpan.FromMinutes(15);

    public async Task<ServicesProjectionResult> ReadAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var nodes = new List<NodeServices>(catalog.Nodes.Count);
        DateTimeOffset? freshUntil = null;
        foreach (var node in catalog.Nodes)
        {
            var observed = await observations.ReadAsync(node, now, cancellationToken);
            var volumes = node.Services.Where(service => service.Kind == ServiceKind.DockerVolume).ToArray();
            foreach (var service in node.Services)
            {
                if (observed.TryGetValue(service.Alias, out var pair) && pair.Latest.Status is "available" or "unavailable")
                {
                    var deadline = pair.Latest.EffectiveAt + Freshness(service.Kind);
                    if (deadline > now && (freshUntil is null || deadline < freshUntil))
                    {
                        freshUntil = deadline;
                    }
                }
            }

            nodes.Add(new NodeServices(node.Alias,
                Project(node, observed, ServiceKind.PostgreSql, now, ServiceMetrics.PostgreSql),
                Project(node, observed, ServiceKind.Redis, now, ServiceMetrics.Redis),
                Project(node, observed, ServiceKind.RabbitMq, now, (latest, _, _) => ServiceMetrics.RabbitMq(latest)),
                Project(node, observed, ServiceKind.Sqlite, now, (latest, _, _) => ServiceMetrics.Sqlite(latest)),
                Project(node, observed, ServiceKind.Nginx, now, ProxyCiDockerMetrics.Nginx),
                Project(node, observed, ServiceKind.NginxAccess, now, (latest, _, _) => ProxyCiDockerMetrics.NginxAccess(latest)),
                Project(node, observed, ServiceKind.Tls, now, (latest, _, _) => ProxyCiDockerMetrics.Tls(latest)),
                Project(node, observed, ServiceKind.Gitea, now, (latest, _, _) => ProxyCiDockerMetrics.Gitea(latest)),
                Project(node, observed, ServiceKind.GiteaActions, now, (latest, _, _) => ProxyCiDockerMetrics.GiteaActions(latest)),
                Project(node, observed, ServiceKind.DockerWorkload, now, (latest, _, _) => ProxyCiDockerMetrics.DockerWorkload(latest, volumes)),
                Project(node, observed, ServiceKind.DockerVolume, now, (latest, _, _) => ProxyCiDockerMetrics.DockerVolume(latest))));
        }

        return new ServicesProjectionResult(nodes, freshUntil);
    }

    private static TimeSpan Freshness(ServiceKind kind) => kind switch
    {
        ServiceKind.DockerVolume => VolumeFreshness,
        ServiceKind.DockerWorkload or ServiceKind.GiteaActions => SnapshotFreshness,
        _ => ProbeFreshness,
    };

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
            var freshness = Freshness(kind);
            if (latest.Status == "stale" || now - latest.EffectiveAt >= freshness)
            {
                return new ServiceReading<T>(service.Alias, "stale", latest.ObservedAt, null);
            }

            if (latest.Status == "unavailable")
            {
                return new ServiceReading<T>(service.Alias, "unavailable", latest.ObservedAt, null);
            }

            var previous = pair.Previous;
            var window = latest.ObservedAt - previous?.ObservedAt;
            var elapsed = previous is { Status: "available" } && previous.BootId == latest.BootId
                && window > TimeSpan.Zero && window <= freshness ? window.Value.TotalSeconds : (double?)null;
            return new ServiceReading<T>(service.Alias, "available", latest.ObservedAt,
                metrics(latest.Data, elapsed.HasValue ? previous!.Data : default, elapsed));
        }).ToArray();
}
