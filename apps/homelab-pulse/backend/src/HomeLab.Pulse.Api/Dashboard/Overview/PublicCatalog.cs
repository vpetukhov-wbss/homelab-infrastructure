using System.Text.RegularExpressions;

namespace HomeLab.Pulse.Api.Dashboard.Overview;

[Flags]
internal enum PublicMetric
{
    None = 0,
    Cpu = 1,
    Memory = 2,
    Swap = 4,
    Disk = 8,
    Network = 16,
    Temperature = 32,
}

internal sealed record PublicNode(
    string AgentId, string Alias, PublicMetric Metrics,
    string? Filesystem, string? NetworkInterface, string? TemperatureSensor);

internal sealed partial class PublicCatalog
{
    public PublicCatalog(IConfiguration configuration)
    {
        var agentIds = configuration.GetSection("Telemetry:Agents").GetChildren()
            .Select(agent => agent.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Nodes = configuration.GetSection("Dashboard:PublicNodes").GetChildren()
            .Take(65).Select(ReadNode).OrderBy(node => node.Alias, StringComparer.Ordinal).ToArray();
        if (Nodes.Count > 64 || Nodes.Any(node => agentIds.Contains(node.Alias))
            || Nodes.Select(node => node.Alias).Distinct(StringComparer.Ordinal).Count() != Nodes.Count)
        {
            throw InvalidCatalog();
        }
    }

    public IReadOnlyList<PublicNode> Nodes { get; }

    private static PublicNode ReadNode(IConfigurationSection section)
    {
        if (!AliasPattern().IsMatch(section.Key) || section["Alias"] is not { } alias || !AliasPattern().IsMatch(alias)
            || string.Equals(section.Key, alias, StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidCatalog();
        }

        var metrics = PublicMetric.None;
        foreach (var item in section.GetSection("Metrics").GetChildren())
        {
            metrics |= item.Value switch
            {
                "cpu" => PublicMetric.Cpu,
                "memory" => PublicMetric.Memory,
                "swap" => PublicMetric.Swap,
                "disk" => PublicMetric.Disk,
                "network" => PublicMetric.Network,
                "temperature" => PublicMetric.Temperature,
                _ => throw InvalidCatalog(),
            };
        }

        return new PublicNode(section.Key, alias, metrics,
            GetSelector(section, "Filesystem", metrics.HasFlag(PublicMetric.Disk)),
            GetSelector(section, "NetworkInterface", metrics.HasFlag(PublicMetric.Network)),
            GetSelector(section, "TemperatureSensor", metrics.HasFlag(PublicMetric.Temperature)));
    }

    private static string? GetSelector(IConfigurationSection section, string key, bool enabled)
    {
        if (!enabled)
        {
            return null;
        }

        return section[key] is { } value && AliasPattern().IsMatch(value) ? value : throw InvalidCatalog();
    }

    private static InvalidOperationException InvalidCatalog() =>
        new("Dashboard catalog requires unique public aliases, supported metrics, and valid source selectors.");

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,31}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex AliasPattern();
}
