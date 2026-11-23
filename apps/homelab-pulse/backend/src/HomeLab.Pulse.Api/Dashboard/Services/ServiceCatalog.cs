namespace HomeLab.Pulse.Api.Dashboard.Services;

internal enum ServiceKind
{
    PostgreSql,
    Redis,
    RabbitMq,
    Sqlite,
    Nginx,
    NginxAccess,
    Tls,
    Gitea,
    GiteaActions,
    DockerWorkload,
    DockerVolume,
}

internal sealed record ApprovedService(string Alias, ServiceKind Kind, string? CollectorAlias);
internal sealed record ServiceNode(string AgentId, string Alias, IReadOnlyList<ApprovedService> Services);

internal sealed class ServiceCatalog
{
    private const int MaxServices = 64;

    public ServiceCatalog(PublicCatalog publicCatalog, IConfiguration configuration)
    {
        var nodes = publicCatalog.Nodes.ToDictionary(node => node.Alias, StringComparer.Ordinal);
        var sections = configuration.GetSection("Dashboard:PublicServices").GetChildren().Take(MaxServices + 1).ToArray();
        if (sections.Length > MaxServices)
        {
            throw InvalidCatalog();
        }

        var privateAliases = configuration.GetSection("Telemetry:Agents").GetChildren().Select(item => item.Key)
            .Concat(publicCatalog.Nodes.Select(node => node.AgentId))
            .Concat(sections.Select(item => item["Source"]).OfType<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = new HashSet<(string AgentId, ServiceKind Kind, string? CollectorAlias)>();
        var approved = new List<(PublicNode Node, ApprovedService Service)>(sections.Length);
        foreach (var entry in sections)
        {
            if (!PublicCatalog.IsAlias(entry.Key) || privateAliases.Contains(entry.Key)
                || entry["Node"] is not { } alias || !nodes.TryGetValue(alias, out var node))
            {
                throw InvalidCatalog();
            }

            var kind = entry["Kind"] switch
            {
                "postgresql" => ServiceKind.PostgreSql,
                "redis" => ServiceKind.Redis,
                "rabbitmq" => ServiceKind.RabbitMq,
                "sqlite" => ServiceKind.Sqlite,
                "nginx" => ServiceKind.Nginx,
                "nginx-access" => ServiceKind.NginxAccess,
                "tls" => ServiceKind.Tls,
                "gitea" => ServiceKind.Gitea,
                "gitea-actions" => ServiceKind.GiteaActions,
                "docker-workload" => ServiceKind.DockerWorkload,
                "docker-volume" => ServiceKind.DockerVolume,
                _ => throw InvalidCatalog(),
            };

            var source = entry["Source"];
            var mappedSource = kind is ServiceKind.Sqlite or ServiceKind.DockerWorkload or ServiceKind.DockerVolume;
            if ((mappedSource ? source is null || !PublicCatalog.IsAlias(source) : source is not null)
                || !selected.Add((node.AgentId, kind, source)))
            {
                throw InvalidCatalog();
            }

            approved.Add((node, new ApprovedService(entry.Key, kind, source)));
        }

        Nodes = approved.GroupBy(item => item.Node)
            .OrderBy(group => group.Key.Alias, StringComparer.Ordinal)
            .Select(group => new ServiceNode(group.Key.AgentId, group.Key.Alias,
                group.Select(item => item.Service).OrderBy(service => service.Alias, StringComparer.Ordinal).ToArray()))
            .ToArray();
    }

    public IReadOnlyList<ServiceNode> Nodes { get; }

    private static InvalidOperationException InvalidCatalog() =>
        new("Dashboard services require valid public aliases, approved nodes, supported kinds and unique collector sources.");
}
