using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace HomeLab.Pulse.Api.IntegrationTests;

public sealed class PulseRealtimeTests : IDisposable
{
    private const string OverviewPath = "/api/v1/overview.json";
    private static readonly DateTimeOffset Now = new(2026, 1, 27, 12, 0, 0, TimeSpan.Zero);
    private readonly DirectoryInfo _temporary = Directory.CreateTempSubdirectory("pulse-realtime-tests-");
    private string DatabasePath => Path.Combine(_temporary.FullName, "pulse.db");

    [Fact]
    public async Task PythonAgentFeedsThePublishedOverviewAndRealtimeClientsRecoverByRevision()
    {
        var clock = new FakeTimeProvider(Now);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var cancellationToken = deadline.Token;
        long lastRevision;

        await using (var app = StartApi(clock))
        {
            using var client = app.CreateClient();
            var initial = await ReadOverviewAfterAsync(client, revision: 0, cancellationToken);
            Assert.Equal("unavailable", Node(initial).GetProperty("status").GetString());

            await using var events = new PulseEvents(client.BaseAddress!);
            await events.ConnectAsync(cancellationToken);
            var payload = await PythonAgentFixture.SendAsync(client.BaseAddress!, cancellationToken, observedAt: Now);
            await AssertOnlyCommittedSampleAsync(payload, cancellationToken);
            clock.Advance(TimeSpan.FromSeconds(2));

            var fresh = await events.ReadAfterAsync(Revision(initial), cancellationToken);
            Assert.Equal("available", Node(fresh).GetProperty("status").GetString());
            Assert.Equal(2048, Node(fresh).GetProperty("metrics").GetProperty("memoryUsedBytes").GetInt64());
            Assert.DoesNotContain("demo-a", fresh.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("sampleId", fresh.GetRawText(), StringComparison.Ordinal);
            var published = await ReadOverviewAfterAsync(client, Revision(initial), cancellationToken);
            Assert.True(JsonElement.DeepEquals(fresh, published));
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(DatabasePath + ".overview.json", cancellationToken));
            Assert.True(JsonElement.DeepEquals(fresh, saved.RootElement));

            await events.DisconnectAsync(cancellationToken);
            clock.Advance(TimeSpan.FromSeconds(90));
            var missed = await ReadOverviewAfterAsync(client, Revision(fresh), cancellationToken);
            await events.ConnectAsync(cancellationToken);
            var resynced = await ReadOverviewAfterAsync(client, Revision(fresh), cancellationToken);
            Assert.True(JsonElement.DeepEquals(missed, resynced));
            Assert.Equal("stale", Node(resynced).GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, Node(resynced).GetProperty("metrics").GetProperty("memoryUsedBytes").ValueKind);
            lastRevision = Revision(resynced);
        }

        await using var restarted = StartApi(clock);
        using var restartedClient = restarted.CreateClient();
        var rebuilt = await ReadOverviewAfterAsync(restartedClient, lastRevision, cancellationToken);
        Assert.Equal("stale", Node(rebuilt).GetProperty("status").GetString());
        await using var reconnected = new PulseEvents(restartedClient.BaseAddress!);
        await reconnected.ConnectAsync(cancellationToken);
        await PythonAgentFixture.SendAsync(restartedClient.BaseAddress!, cancellationToken, observedAt: clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(2));

        var updated = await reconnected.ReadAfterAsync(Revision(rebuilt), cancellationToken);
        Assert.Equal("available", Node(updated).GetProperty("status").GetString());
        Assert.True(JsonElement.DeepEquals(updated, await ReadOverviewAfterAsync(restartedClient, Revision(rebuilt), cancellationToken)));
    }

    public void Dispose() => _temporary.Delete(recursive: true);

    private PulseApiFactory StartApi(TimeProvider clock)
    {
        var app = new PulseApiFactory(DatabasePath, clock: clock, dashboard: new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Dashboard:PublicNodes:demo-a:Alias"] = "demo-edge",
            ["Dashboard:PublicNodes:demo-a:Metrics:0"] = "memory",
        });
        app.UseKestrel(0);
        return app;
    }

    private async Task AssertOnlyCommittedSampleAsync(string payload, CancellationToken cancellationToken)
    {
        await using var database = PulseApiFactory.Connect(DatabasePath);
        await database.OpenAsync(cancellationToken);
        await using var command = database.CreateCommand();
        command.CommandText = "SELECT Payload FROM TelemetrySamples";
        await using var rows = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await rows.ReadAsync(cancellationToken));
        Assert.Equal(payload, rows.GetString(0));
        Assert.False(await rows.ReadAsync(cancellationToken));
    }

    private static JsonElement Node(JsonElement overview) => overview.GetProperty("nodes").EnumerateArray().Single();

    private static long Revision(JsonElement overview) => overview.GetProperty("revision").GetInt64();

    private static async Task<JsonElement> ReadOverviewAfterAsync(HttpClient client, long revision, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var response = await client.GetAsync(OverviewPath, cancellationToken);
            if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
            {
                response.EnsureSuccessStatusCode();
                var content = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var json = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
                if (Revision(json.RootElement) > revision)
                {
                    return json.RootElement.Clone();
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
    }
}
