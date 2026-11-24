using System.Text.Json;
using HomeLab.Pulse.Api.Dashboard.Realtime;
using HomeLab.Pulse.Api.Telemetry.Persistence;

namespace HomeLab.Pulse.Api.Dashboard.Overview;

internal sealed record PublishedOverview
{
    public required OverviewSnapshot Snapshot { get; init; }

    public required byte[] State { get; init; }

    public required byte[] Json { get; init; }

    public DateTimeOffset? RefreshDeadline { get; init; }
}

internal sealed class OverviewPublication(TelemetryStorageSettings storage, PulseBroadcastWorker broadcasts)
{
    private readonly string _path = storage.DatabasePath + ".overview.json";
    private PublishedOverview? _current;

    public PublishedOverview? Current => Volatile.Read(ref _current);

    public async Task PublishAsync(OverviewSnapshot snapshot, CancellationToken cancellationToken)
    {
        var state = JsonSerializer.SerializeToUtf8Bytes(snapshot.Nodes, JsonSerializerOptions.Web);
        var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonSerializerOptions.Web);
        var deadline = snapshot.Nodes.Where(node => node.Status == "available").Min(node => node.ObservedAt)
            + HostTelemetry.Freshness + OverviewWorker.Frequency;
        var published = new PublishedOverview
        {
            Snapshot = snapshot,
            State = state,
            Json = json,
            RefreshDeadline = deadline,
        };
        await DashboardSnapshotFile.ReplaceAsync(_path, json, cancellationToken);
        Volatile.Write(ref _current, published);
        broadcasts.Publish(snapshot);
    }
}
