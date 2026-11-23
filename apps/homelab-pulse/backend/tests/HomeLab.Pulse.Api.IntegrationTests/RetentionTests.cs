using HomeLab.Pulse.Api.Telemetry.Retention;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace HomeLab.Pulse.Api.IntegrationTests;

public sealed class RetentionTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 27, 12, 0, 0, TimeSpan.Zero);
    private readonly DirectoryInfo _temporary = Directory.CreateTempSubdirectory("pulse-retention-tests-");
    private string DatabasePath => Path.Combine(_temporary.FullName, "pulse.db");

    [Fact]
    public async Task SweepDeletesInBoundedOldestFirstBatchesAndKeepsTheExactCutoff()
    {
        await using var app = new PulseApiFactory(
            DatabasePath, clock: new FakeTimeProvider(Now), retentionBatchSize: 1, runRetentionWorker: false);
        var cutoff = Now.AddDays(-7);
        await using var connection = await OpenAsync(app);

        // Insert the latest expired records first. The cutoff uses observation time, not receipt time.
        await InsertAsync(connection, 130, cutoff.AddTicks(-1), Now);
        await InsertAsync(connection, 129, cutoff.AddTicks(-1), Now);
        for (var marker = 128; marker >= 1; marker--)
        {
            await InsertAsync(connection, marker, cutoff.AddSeconds(-marker), Now);
        }

        await InsertAsync(connection, 131, cutoff, Now);
        await InsertAsync(connection, 132, cutoff.AddTicks(1), Now);

        await using var scope = app.Services.CreateAsyncScope();
        var sweep = scope.ServiceProvider.GetRequiredService<RetentionSweep>();
        await sweep.RunAsync(CancellationToken.None);
        Assert.Equal([129L, 130L, 131L, 132L], await MarkersAsync(connection));

        await sweep.RunAsync(CancellationToken.None);
        Assert.Equal([131L, 132L], await MarkersAsync(connection));
        await sweep.RunAsync(CancellationToken.None);
        Assert.Equal([131L, 132L], await MarkersAsync(connection));
    }

    [Fact]
    public async Task WorkerRunsAtStartupAndOnTheHourlyTick()
    {
        var clock = new FakeTimeProvider(Now);
        var cutoff = Now.AddDays(-7);
        await using (var setup = new PulseApiFactory(DatabasePath, clock: clock, runRetentionWorker: false))
        {
            await using var seed = await OpenAsync(setup);
            await InsertAsync(seed, 1, cutoff.AddTicks(-1), Now);
            await InsertAsync(seed, 2, cutoff, Now);
            await InsertAsync(seed, 3, cutoff.AddHours(1), Now);
        }

        await using var app = new PulseApiFactory(DatabasePath, clock: clock);
        await using var connection = await OpenAsync(app);
        await WaitForMarkersAsync(connection, [2L, 3L]);

        clock.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal([2L, 3L], await MarkersAsync(connection));
        clock.Advance(TimeSpan.FromMinutes(1));
        await WaitForMarkersAsync(connection, [3L]);
    }

    [Fact]
    public async Task LockedStorageDoesNotLoseTheSampleAndTheNextSweepRecovers()
    {
        await using var app = new PulseApiFactory(
            DatabasePath, clock: new FakeTimeProvider(Now), runRetentionWorker: false);
        await using var blocker = await OpenAsync(app);
        await InsertAsync(blocker, 1, Now.AddDays(-8), Now);
        await using var scope = app.Services.CreateAsyncScope();
        var sweep = scope.ServiceProvider.GetRequiredService<RetentionSweep>();
        using (var writeLock = blocker.BeginTransaction(deferred: false))
        {
            await sweep.RunAsync(CancellationToken.None);
            writeLock.Rollback();
        }

        Assert.Equal([1L], await MarkersAsync(blocker));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sweep.RunAsync(cancelled.Token));
        Assert.Equal([1L], await MarkersAsync(blocker));
        await sweep.RunAsync(CancellationToken.None);
        Assert.Empty(await MarkersAsync(blocker));
    }

    [Fact]
    public async Task StartupRejectsAnOutOfRangeBatchSize()
    {
        await using var app = new PulseApiFactory(DatabasePath, retentionBatchSize: 1025);
        Assert.Throws<OptionsValidationException>(() => app.CreateClient());
    }

    public void Dispose() => _temporary.Delete(recursive: true);

    private async Task<SqliteConnection> OpenAsync(PulseApiFactory app)
    {
        _ = app.Services; // Start the host and apply migrations before seeding or reading.
        var connection = PulseApiFactory.Connect(DatabasePath);
        try
        {
            await connection.OpenAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task InsertAsync(SqliteConnection connection, long marker, DateTimeOffset observedAt, DateTimeOffset receivedAt)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO "TelemetrySamples" ("AgentId", "SampleId", "ObservedAtUtcTicks", "ReceivedAtUtcTicks", "Payload")
            VALUES ('demo-a', $id, $observed, $received, $marker)
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid());
        command.Parameters.AddWithValue("$observed", observedAt.UtcTicks);
        command.Parameters.AddWithValue("$received", receivedAt.UtcTicks);
        command.Parameters.AddWithValue("$marker", marker);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<long>> MarkersAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """SELECT CAST("Payload" AS INTEGER) AS Marker FROM "TelemetrySamples" ORDER BY Marker""";
        await using var reader = await command.ExecuteReaderAsync();
        var markers = new List<long>();
        while (await reader.ReadAsync())
        {
            markers.Add(reader.GetInt64(0));
        }

        return markers;
    }

    private static async Task WaitForMarkersAsync(SqliteConnection connection, long[] expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!expected.SequenceEqual(await MarkersAsync(connection)))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }
    }
}
