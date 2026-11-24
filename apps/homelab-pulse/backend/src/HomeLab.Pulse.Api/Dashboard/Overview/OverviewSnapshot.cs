namespace HomeLab.Pulse.Api.Dashboard.Overview;

public sealed record OverviewSnapshot
{
    public required int SchemaVersion { get; init; }

    public required long Revision { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    public required IReadOnlyList<OverviewNode> Nodes { get; init; }
}
