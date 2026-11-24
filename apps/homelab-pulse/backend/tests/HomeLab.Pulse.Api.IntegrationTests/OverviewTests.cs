using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace HomeLab.Pulse.Api.IntegrationTests;

public sealed class OverviewTests : IDisposable
{
    private const string OverviewPath = "/api/v1/overview.json";
    private static readonly DateTimeOffset Now = new(2026, 1, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid BootId = new("a7f009fd-f2db-4d63-b629-2a6c592bb5f4");
    private readonly DirectoryInfo _temporary = Directory.CreateTempSubdirectory("pulse-overview-tests-");
    private string DatabasePath => Path.Combine(_temporary.FullName, "pulse.db");
    private string SnapshotPath => DatabasePath + ".overview.json";

    [Fact]
    public async Task DefaultCatalogPublishesAnEmptyOverviewWithNoStore()
    {
        await using var app = new PulseApiFactory(DatabasePath);
        using var client = app.CreateClient();
        var empty = await WaitForOverviewAsync(client, root => root.GetProperty("nodes").GetArrayLength() == 0);

        Assert.Equal(1, empty.GetProperty("schemaVersion").GetInt32());
        Assert.True(Revision(empty) > 0);
        Assert.Equal(TimeSpan.Zero, empty.GetProperty("generatedAt").GetDateTimeOffset().Offset);
        await AcceptAsync(client, Sample(DateTimeOffset.UtcNow), PulseApiFactory.AgentToken);
        using var response = await client.GetAsync(OverviewPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(empty.GetRawText(), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OnlyApprovedFieldsAndAliasesReachThePublishedSnapshot()
    {
        var clock = new FakeTimeProvider(Now);
        var catalog = Catalog();
        catalog["Dashboard:PublicNodes:absent:Alias"] = "quiet-node";
        await using var app = new PulseApiFactory(DatabasePath, clock: clock, dashboard: catalog);
        using var client = app.CreateClient();
        var empty = await WaitForOverviewAsync(client, root => Node(root).GetProperty("status").GetString() == "unavailable");
        Assert.Null(Node(empty).GetProperty("observedAt").GetString());

        await AcceptAsync(client, Sample(Now.AddSeconds(-12), user: 10, idle: 30, received: 100, sent: 300), PulseApiFactory.AgentToken);
        await AcceptAsync(client, Sample(Now.AddSeconds(-2), user: 14, idle: 36, received: 300, sent: 400), PulseApiFactory.AgentToken);
        await AcceptAsync(client, Sample(Now, available: 997), PulseApiFactory.OtherAgentToken);
        using var rejected = await PostAsync(client, Sample(Now, available: 998).Insert(1, "\"agentId\":\"demo-a\","),
            PulseApiFactory.AgentToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);
        clock.Advance(TimeSpan.FromSeconds(2));

        var published = await WaitForOverviewAsync(client, root => Revision(root) > Revision(empty));
        var node = Node(published);
        Assert.Equal("available", node.GetProperty("status").GetString());
        Assert.Equal(Now.AddSeconds(-2), node.GetProperty("observedAt").GetDateTimeOffset());
        Assert.Equal("unavailable", Node(published, "quiet-node").GetProperty("status").GetString());
        var metrics = node.GetProperty("metrics");
        Assert.Equal(40, metrics.GetProperty("cpuUsagePercent").GetDouble());
        Assert.Equal(750, metrics.GetProperty("memoryUsedBytes").GetInt64());
        Assert.Equal(1000, metrics.GetProperty("memoryTotalBytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, metrics.GetProperty("swapUsedBytes").ValueKind);
        Assert.Equal(120, metrics.GetProperty("diskAvailableBytes").GetInt64());
        Assert.Equal(500, metrics.GetProperty("diskTotalBytes").GetInt64());
        Assert.Equal(20, metrics.GetProperty("networkRxBytesPerSecond").GetDouble());
        Assert.Equal(10, metrics.GetProperty("networkTxBytesPerSecond").GetDouble());
        Assert.Equal(43.5, metrics.GetProperty("temperatureCelsius").GetDouble());

        var json = published.GetRawText();
        foreach (var privateValue in new[] { "demo-a", "demo-b", "private-root", "private-link", "private-sensor",
            "unused-secret", "bootId", "sampleId", "values", "loadAverage", "997", "998", "98765" })
        {
            Assert.DoesNotContain(privateValue, json, StringComparison.Ordinal);
        }

        Assert.Equal(json, await File.ReadAllTextAsync(SnapshotPath));
    }

    [Fact]
    public async Task OlderUploadsAndDuplicatesCannotReplaceTheLatestObservation()
    {
        var clock = new FakeTimeProvider(Now);
        await using var app = new PulseApiFactory(DatabasePath, clock: clock, dashboard: Catalog());
        using var client = app.CreateClient();
        var empty = await WaitForOverviewAsync(client, root => Node(root).GetProperty("status").GetString() == "unavailable");
        var sampleId = Guid.NewGuid();
        await AcceptAsync(client, Sample(Now, available: 210, sampleId: sampleId), PulseApiFactory.AgentToken);
        clock.Advance(TimeSpan.FromSeconds(2));
        var first = await WaitForOverviewAsync(client, root => Revision(root) > Revision(empty));

        await AcceptAsync(client, Sample(Now.AddSeconds(-91), available: 850), PulseApiFactory.AgentToken);
        await AcceptAsync(client, Sample(Now.AddSeconds(1), available: 800, sampleId: sampleId), PulseApiFactory.AgentToken);
        await AcceptAsync(client, Sample(Now.AddSeconds(8), available: 450, bootId: Guid.NewGuid()), PulseApiFactory.AgentToken);
        clock.Advance(TimeSpan.FromSeconds(2));
        using (var response = await client.GetAsync(OverviewPath))
        {
            Assert.Equal(first.GetRawText(), await response.Content.ReadAsStringAsync());
        }

        clock.Advance(TimeSpan.FromSeconds(4));
        var rebooted = await WaitForOverviewAsync(client, root => Revision(root) > Revision(first));
        var current = Node(rebooted);
        Assert.Equal(Now.AddSeconds(8), current.GetProperty("observedAt").GetDateTimeOffset());
        var metrics = current.GetProperty("metrics");
        Assert.Equal(550, metrics.GetProperty("memoryUsedBytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, metrics.GetProperty("cpuUsagePercent").ValueKind);
        Assert.Equal(JsonValueKind.Null, metrics.GetProperty("networkRxBytesPerSecond").ValueKind);

        clock.Advance(TimeSpan.FromSeconds(90));
        var stale = await WaitForOverviewAsync(client, root => Node(root).GetProperty("status").GetString() == "stale");
        Assert.Equal(Now.AddSeconds(8), Node(stale).GetProperty("observedAt").GetDateTimeOffset());
        Assert.All(Node(stale).GetProperty("metrics").EnumerateObject(),
            metric => Assert.Equal(JsonValueKind.Null, metric.Value.ValueKind));
    }

    [Fact]
    public async Task AFailedFileReplacementDoesNotPublishAndRetriesTheCommittedSample()
    {
        var clock = new FakeTimeProvider(Now);
        Directory.CreateDirectory(SnapshotPath);
        using var failures = new PublicationFailures();
        await using var app = new PulseApiFactory(DatabasePath, clock: clock, dashboard: Catalog(), logging: failures);
        app.UseKestrel(0);
        using var client = app.CreateClient();
        await failures.WaitAsync();
        var failedRevision = await AllocatedRevisionAsync();
        using var waiting = await client.GetAsync(OverviewPath);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, waiting.StatusCode);
        Assert.True(waiting.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain(DatabasePath, await waiting.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Directory.Delete(SnapshotPath);
        clock.Advance(TimeSpan.FromSeconds(2));
        var first = await WaitForOverviewAsync(client, root => Revision(root) > failedRevision);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var events = new PulseEvents(client.BaseAddress!);
        await events.ConnectAsync(deadline.Token);
        File.Move(SnapshotPath, Path.Combine(_temporary.FullName, "backup.json"));
        Directory.CreateDirectory(SnapshotPath);

        await AcceptAsync(client, Sample(Now), PulseApiFactory.AgentToken);
        clock.Advance(TimeSpan.FromSeconds(2));
        await failures.WaitAsync();
        var secondFailure = await AllocatedRevisionAsync();
        using var cached = await client.GetAsync(OverviewPath);
        Assert.Equal(first.GetRawText(), await cached.Content.ReadAsStringAsync());

        Directory.Delete(SnapshotPath);
        clock.Advance(TimeSpan.FromSeconds(2));
        var recovered = await WaitForOverviewAsync(client, root => Revision(root) > secondFailure);
        Assert.Equal("available", Node(recovered).GetProperty("status").GetString());
        Assert.Equal(recovered.GetRawText(), await File.ReadAllTextAsync(SnapshotPath));
        var notification = await events.ReadAfterAsync(Revision(first), deadline.Token);
        Assert.True(JsonElement.DeepEquals(recovered, notification));

        File.Move(SnapshotPath, Path.Combine(_temporary.FullName, "recovered.json"));
        Directory.CreateDirectory(SnapshotPath);
        clock.Advance(TimeSpan.FromSeconds(92));
        await failures.WaitAsync();
        using var expired = await client.GetAsync(OverviewPath);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, expired.StatusCode);
    }

    [Fact]
    public async Task RestartRebuildsWithTheNewCatalogAndNeverServesTheOldFile()
    {
        var clock = new FakeTimeProvider(Now);
        long revision;
        await using (var app = new PulseApiFactory(DatabasePath, clock: clock, dashboard: Catalog()))
        {
            using var client = app.CreateClient();
            await WaitForOverviewAsync(client, root => Node(root).GetProperty("status").GetString() == "unavailable");
            await AcceptAsync(client, Sample(Now), PulseApiFactory.AgentToken);
            clock.Advance(TimeSpan.FromSeconds(2));
            var published = await WaitForOverviewAsync(client, root => Node(root).GetProperty("status").GetString() == "available");
            revision = Revision(published);
        }

        var newCatalog = Catalog();
        newCatalog["Dashboard:PublicNodes:demo-a:Alias"] = "demo-rebuilt";
        await File.WriteAllTextAsync(SnapshotPath, """{"unapproved":"old-unsafe-file"}""");
        await using var restarted = new PulseApiFactory(DatabasePath, clock: clock, dashboard: newCatalog);
        using var restartedClient = restarted.CreateClient();
        var rebuilt = await WaitForOverviewAsync(restartedClient, root => Revision(root) > revision);
        Assert.Equal(750, Node(rebuilt, "demo-rebuilt").GetProperty("metrics").GetProperty("memoryUsedBytes").GetInt64());
        Assert.DoesNotContain("demo-edge", rebuilt.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("old-unsafe-file", rebuilt.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(rebuilt.GetRawText(), await File.ReadAllTextAsync(SnapshotPath));
    }

    [Fact]
    public async Task CatalogRejectsAnAuthenticatedAgentIdAsAPublicAlias()
    {
        var catalog = Catalog();
        catalog["Dashboard:PublicNodes:demo-a:Alias"] = "demo-b";
        await using var app = new PulseApiFactory(DatabasePath, dashboard: catalog);
        Assert.Throws<InvalidOperationException>(() => app.CreateClient());
    }

    public void Dispose() => _temporary.Delete(recursive: true);

    private static Dictionary<string, string?> Catalog() => new(StringComparer.Ordinal)
    {
        ["Dashboard:PublicNodes:demo-a:Alias"] = "demo-edge",
        ["Dashboard:PublicNodes:demo-a:Metrics:0"] = "memory",
        ["Dashboard:PublicNodes:demo-a:Metrics:1"] = "cpu",
        ["Dashboard:PublicNodes:demo-a:Metrics:2"] = "disk",
        ["Dashboard:PublicNodes:demo-a:Metrics:3"] = "network",
        ["Dashboard:PublicNodes:demo-a:Metrics:4"] = "temperature",
        ["Dashboard:PublicNodes:demo-a:Filesystem"] = "private-root",
        ["Dashboard:PublicNodes:demo-a:NetworkInterface"] = "private-link",
        ["Dashboard:PublicNodes:demo-a:TemperatureSensor"] = "private-sensor",
    };

    private static JsonElement Node(JsonElement root, string alias = "demo-edge") =>
        root.GetProperty("nodes").EnumerateArray().Single(node => node.GetProperty("alias").GetString() == alias);

    private static long Revision(JsonElement root) => root.GetProperty("revision").GetInt64();

    private static async Task<JsonElement> WaitForOverviewAsync(HttpClient client, Func<JsonElement, bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            using var response = await client.GetAsync(OverviewPath, timeout.Token);
            if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
            {
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                if (ready(json.RootElement))
                {
                    return json.RootElement.Clone();
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }
    }

    private async Task<long> AllocatedRevisionAsync()
    {
        await using var connection = PulseApiFactory.Connect(DatabasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """SELECT "Value" FROM "DashboardRevision" WHERE "Id" = 1""";
        return Assert.IsType<long>(await command.ExecuteScalarAsync());
    }

    private static async Task AcceptAsync(HttpClient client, string sample, string token)
    {
        using var response = await PostAsync(client, sample, token);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string sample, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/telemetry");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(sample, Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }

    private static string Sample(DateTimeOffset observed, int available = 250, int user = 1, int idle = 1,
        int received = 0, int sent = 0, Guid? sampleId = null, Guid? bootId = null) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            sampleId = sampleId ?? Guid.NewGuid(),
            observedAt = observed.UtcDateTime,
            kind = "host",
            values = new
            {
                bootId = bootId ?? BootId,
                cpuTimesSeconds = new { user, nice = 0, system = 0, idle, iowait = 0, irq = 0, softirq = 0, steal = 0 },
                memory = new { totalBytes = 1000, availableBytes = available, swapTotalBytes = 98765, swapFreeBytes = 0 },
                loadAverage = (int[])[0, 0, 0],
                uptimeSeconds = 1,
                filesystems = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["private-root"] = new { totalBytes = 500, availableBytes = 120 },
                    ["unused-secret"] = new { totalBytes = 800, availableBytes = 300 },
                },
                network = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["private-link"] = new { rxBytes = received, txBytes = sent },
                },
                temperaturesCelsius = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["private-sensor"] = 43.5,
                    ["unused-secret"] = 88.5,
                },
            },
        }, JsonSerializerOptions.Web);

    private sealed class PublicationFailures : ILoggerProvider, ILogger
    {
        private readonly SemaphoreSlim _events = new(0);

        public async Task WaitAsync() => Assert.True(await _events.WaitAsync(TimeSpan.FromSeconds(10)));

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel == LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Name == "LogPublicationFailure")
            {
                _events.Release();
            }
        }

        public void Dispose() => _events.Dispose();
    }
}
