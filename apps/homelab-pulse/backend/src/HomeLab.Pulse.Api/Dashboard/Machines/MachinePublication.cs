using System.Text.Json;
using HomeLab.Pulse.Api.Telemetry.Persistence;

namespace HomeLab.Pulse.Api.Dashboard.Machines;

internal sealed record PublishedMachine(byte[] State, byte[] Json, DateTimeOffset? ExpiresAt);

internal sealed class MachinePublication(TelemetryStorageSettings storage)
{
    private readonly string _path = storage.DatabasePath + ".machines.json";
    private Dictionary<string, PublishedMachine>? _current;

    public PublishedMachine? Find(string alias) => Volatile.Read(ref _current)?.GetValueOrDefault(alias);

    public async Task PublishAsync(IReadOnlyList<MachineDetails> machines, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var previous = Volatile.Read(ref _current);
        var states = machines.Select(item => JsonSerializer.SerializeToUtf8Bytes(item, JsonSerializerOptions.Web)).ToArray();
        if (previous is not null && states.Length == previous.Count && machines.Select((item, i) =>
            previous.TryGetValue(item.Alias, out var entry) && states[i].AsSpan().SequenceEqual(entry.State)).All(equal => equal))
        {
            return;
        }

        var snapshots = machines.Select(item => new MachineSnapshot(1, now, item)).ToArray();
        var next = new Dictionary<string, PublishedMachine>(snapshots.Length, StringComparer.Ordinal);
        for (var i = 0; i < snapshots.Length; i++)
        {
            var snapshot = snapshots[i];
            next.Add(snapshot.Machine.Alias, new PublishedMachine(states[i],
                JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonSerializerOptions.Web), Expiration(snapshot.Machine)));
        }

        await DashboardSnapshotFile.ReplaceAsync(_path,
            JsonSerializer.SerializeToUtf8Bytes(snapshots, JsonSerializerOptions.Web), cancellationToken);
        Volatile.Write(ref _current, next);
    }

    private static DateTimeOffset? Expiration(MachineDetails machine)
    {
        var host = machine.Host.Status == "available" ? machine.Host.ObservedAt + HostTelemetry.Freshness : null;
        var smart = machine.Disks.Where(disk => disk.Smart.Status == "available")
            .Min(disk => disk.Smart.ObservedAt) + SmartProjection.Freshness;
        var earliest = host is null || smart < host ? smart : host;
        return earliest + MachineWorker.Frequency;
    }
}
