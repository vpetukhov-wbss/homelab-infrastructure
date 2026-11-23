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
            + OverviewProjection.Freshness + OverviewWorker.Frequency;
        var published = new PublishedOverview(snapshot, json, deadline);
        var tempPath = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, json, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, _path, overwrite: true);
            Volatile.Write(ref _current, published);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
