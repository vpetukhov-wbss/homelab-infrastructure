using System.Text.Json;
using HomeLab.Pulse.Api.Dashboard.Signals;

namespace HomeLab.Pulse.Api.Dashboard.Services;

internal sealed record ServicesProjectionResult(IReadOnlyList<NodeServices> Nodes, DateTimeOffset? FreshUntil);

internal sealed class ServicesProjection(
    ServiceCatalog catalog,
    ServiceObservations observations,
    ServiceHealthSignals healthSignals)
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

            var context = new ServiceProjectionContext(node, observed, now);
            nodes.Add(new NodeServices
            {
                Alias = node.Alias,
                PostgreSql = Project(context, ServiceKind.PostgreSql, ServiceMetrics.PostgreSql),
                Redis = Project(context, ServiceKind.Redis, ServiceMetrics.Redis),
                RabbitMq = Project(context, ServiceKind.RabbitMq, (latest, _, _) => ServiceMetrics.RabbitMq(latest)),
                Sqlite = Project(context, ServiceKind.Sqlite, (latest, _, _) => ServiceMetrics.Sqlite(latest)),
                Nginx = Project(context, ServiceKind.Nginx, ProxyCiDockerMetrics.Nginx),
                NginxAccess = Project(context, ServiceKind.NginxAccess, (latest, _, _) => ProxyCiDockerMetrics.NginxAccess(latest)),
                Tls = Project(context, ServiceKind.Tls, (latest, _, _) => ProxyCiDockerMetrics.Tls(latest)),
                Gitea = Project(context, ServiceKind.Gitea, (latest, _, _) => ProxyCiDockerMetrics.Gitea(latest)),
                GiteaActions = Project(context, ServiceKind.GiteaActions, (latest, _, _) => ProxyCiDockerMetrics.GiteaActions(latest)),
                DockerWorkloads = Project(context, ServiceKind.DockerWorkload,
                    (latest, _, _) => ProxyCiDockerMetrics.DockerWorkload(latest, volumes)),
                DockerVolumes = Project(context, ServiceKind.DockerVolume, (latest, _, _) => ProxyCiDockerMetrics.DockerVolume(latest)),
            });
        }

        return new ServicesProjectionResult(nodes, freshUntil);
    }

    private static TimeSpan Freshness(ServiceKind kind) => kind switch
    {
        ServiceKind.DockerVolume => VolumeFreshness,
        ServiceKind.DockerWorkload or ServiceKind.GiteaActions => SnapshotFreshness,
        _ => ProbeFreshness,
    };

    private ServiceReading<T>[] Project<T>(
        ServiceProjectionContext context,
        ServiceKind kind,
        Func<JsonElement, JsonElement, double?, T> metrics) where T : class =>
        context.Node.Services.Where(service => service.Kind == kind).Select(service =>
        {
            if (!context.Observed.TryGetValue(service.Alias, out var pair))
            {
                return CreateReading(new ServiceSignalInput<T>
                {
                    Kind = kind,
                    Status = "unknown",
                    ObservedAt = null,
                    Metrics = null,
                    EvaluatedAt = context.EvaluatedAt,
                }, service.Alias);
            }

            var latest = pair.Latest;
            var freshness = Freshness(kind);
            if (latest.Status == "stale" || context.EvaluatedAt - latest.EffectiveAt >= freshness)
            {
                return CreateReading(new ServiceSignalInput<T>
                {
                    Kind = kind,
                    Status = "stale",
                    ObservedAt = latest.ObservedAt,
                    Metrics = null,
                    EvaluatedAt = context.EvaluatedAt,
                }, service.Alias);
            }

            if (latest.Status == "unavailable")
            {
                return CreateReading(new ServiceSignalInput<T>
                {
                    Kind = kind,
                    Status = "unavailable",
                    ObservedAt = latest.ObservedAt,
                    Metrics = null,
                    EvaluatedAt = context.EvaluatedAt,
                }, service.Alias);
            }

            var previous = pair.Previous;
            var window = latest.ObservedAt - previous?.ObservedAt;
            var elapsed = previous is { Status: "available" } && previous.BootId == latest.BootId
                && window > TimeSpan.Zero && window <= freshness ? window.Value.TotalSeconds : (double?)null;
            return CreateReading(new ServiceSignalInput<T>
            {
                Kind = kind,
                Status = "available",
                ObservedAt = latest.ObservedAt,
                Metrics = metrics(latest.Data, elapsed.HasValue ? previous!.Data : default, elapsed),
                EvaluatedAt = context.EvaluatedAt,
            }, service.Alias);
        }).ToArray();

    private ServiceReading<T> CreateReading<T>(ServiceSignalInput<T> input, string alias) where T : class => new()
        {
            Alias = alias,
            Status = input.Status,
            ObservedAt = input.ObservedAt,
            Metrics = input.Metrics,
            Signals = healthSignals.Evaluate(input),
        };

    private sealed record ServiceProjectionContext(
        ServiceNode Node,
        Dictionary<string, ServiceObservationPair> Observed,
        DateTimeOffset EvaluatedAt);
}
