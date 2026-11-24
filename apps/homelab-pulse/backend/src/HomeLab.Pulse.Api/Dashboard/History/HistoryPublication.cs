using System.Text.Json;
using HomeLab.Pulse.Api.Telemetry.Persistence;

namespace HomeLab.Pulse.Api.Dashboard.History;

internal sealed record PublishedHistory(byte[] Json, DateTimeOffset ExpiresAt);

internal sealed class HistoryPublication(TelemetryStorageSettings storage)
{
    private readonly string _pathPrefix = storage.DatabasePath + ".history-";
    private IReadOnlyDictionary<string, PublishedHistory>? _fleet;
    private IReadOnlyDictionary<string, MachineHistory> _machines = new Dictionary<string, MachineHistory>();

    public PublishedHistory? FindFleet(string window) => Volatile.Read(ref _fleet)?.GetValueOrDefault(window);

    public MachineHistory? FindMachine(string alias) => Volatile.Read(ref _machines).GetValueOrDefault(alias);

    public async Task PublishAsync(HistoryProjectionResult projection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var fleet = new Dictionary<string, PublishedHistory>(StringComparer.Ordinal);
        foreach (var item in projection.Fleet)
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(new FleetHistorySnapshot(1, now, item.Value), JsonSerializerOptions.Web);
            await DashboardSnapshotFile.ReplaceAsync(_pathPrefix + item.Key + ".json", json, cancellationToken);
            fleet.Add(item.Key, new PublishedHistory(json, now + HistoryWorker.MaximumSnapshotAge));
        }

        Volatile.Write(ref _machines, projection.Machines);
        Volatile.Write(ref _fleet, fleet);
    }
}
