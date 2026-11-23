using System.Text.Json;
using HomeLab.Pulse.Api.Telemetry.Persistence;

namespace HomeLab.Pulse.Api.Dashboard.Services;

internal sealed record PublishedServices(byte[] State, byte[] Json, DateTimeOffset? ExpiresAt);

internal sealed class ServicesPublication(TelemetryStorageSettings storage)
{
    private readonly string _path = storage.DatabasePath + ".services.json";
    private PublishedServices? _current;

    public PublishedServices? Current => Volatile.Read(ref _current);

    public async Task PublishAsync(IReadOnlyList<NodeServices> nodes, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var state = JsonSerializer.SerializeToUtf8Bytes(nodes, JsonSerializerOptions.Web);
        if (Current is { } existing && state.AsSpan().SequenceEqual(existing.State))
        {
            return;
        }

        var oldestCurrent = nodes.SelectMany(node => CurrentTimes(node.PostgreSql).Concat(CurrentTimes(node.Redis))
            .Concat(CurrentTimes(node.RabbitMq)).Concat(CurrentTimes(node.Sqlite))).Min();
        var expiry = oldestCurrent + ServicesProjection.Freshness + ServicesWorker.Frequency;
        var json = JsonSerializer.SerializeToUtf8Bytes(new ServicesSnapshot(1, now, nodes), JsonSerializerOptions.Web);
        await DashboardSnapshotFile.ReplaceAsync(_path, json, cancellationToken);
        Volatile.Write(ref _current, new PublishedServices(state, json, expiry));
    }

    private static IEnumerable<DateTimeOffset?> CurrentTimes<T>(IReadOnlyList<ServiceReading<T>> readings) where T : class =>
        readings.Where(reading => reading.Status is "available" or "unavailable").Select(reading => reading.ObservedAt);
}
