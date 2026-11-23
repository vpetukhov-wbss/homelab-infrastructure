using System.Text.Json;
using HomeLab.Pulse.Api.Telemetry.Persistence;

namespace HomeLab.Pulse.Api.Dashboard.Overview;

internal sealed record PublishedOverview(OverviewSnapshot Snapshot, byte[] Json, DateTimeOffset? RefreshDeadline);

internal sealed class OverviewPublication(TelemetryStorageSettings storage)
{
    private readonly string _path = storage.DatabasePath + ".overview.json";
    private PublishedOverview? _current;

    public PublishedOverview? Current => Volatile.Read(ref _current);

    public async Task PublishAsync(OverviewSnapshot snapshot, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonSerializerOptions.Web);
        var deadline = snapshot.Nodes.Where(node => node.Status == "available").Min(node => node.ObservedAt)
            + HostTelemetry.Freshness + OverviewWorker.Frequency;
        var published = new PublishedOverview(snapshot, json, deadline);
        await DashboardSnapshotFile.ReplaceAsync(_path, json, cancellationToken);
        Volatile.Write(ref _current, published);
    }
}
