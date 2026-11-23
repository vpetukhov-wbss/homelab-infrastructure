using System.Text.Json;
using HomeLab.Pulse.Api.Telemetry.Persistence;

namespace HomeLab.Pulse.Api.Dashboard.Services;

internal sealed record PublishedServices(byte[] State, byte[] Json, DateTimeOffset? ExpiresAt);

internal sealed class ServicesPublication(TelemetryStorageSettings storage)
{
    private readonly string _path = storage.DatabasePath + ".services.json";
    private PublishedServices? _current;

    public PublishedServices? Current => Volatile.Read(ref _current);

    public async Task PublishAsync(ServicesProjectionResult projection, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var state = JsonSerializer.SerializeToUtf8Bytes(projection.Nodes, JsonSerializerOptions.Web);
        var expiry = projection.FreshUntil + ServicesWorker.Frequency;
        if (Current is { } existing && state.AsSpan().SequenceEqual(existing.State))
        {
            if (existing.ExpiresAt != expiry)
            {
                Volatile.Write(ref _current, existing with { ExpiresAt = expiry });
            }

            return;
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(new ServicesSnapshot(1, now, projection.Nodes), JsonSerializerOptions.Web);
        await DashboardSnapshotFile.ReplaceAsync(_path, json, cancellationToken);
        Volatile.Write(ref _current, new PublishedServices(state, json, expiry));
    }
}
