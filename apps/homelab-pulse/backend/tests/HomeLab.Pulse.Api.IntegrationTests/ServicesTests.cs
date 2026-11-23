using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace HomeLab.Pulse.Api.IntegrationTests;

public sealed partial class ServicesTests : IDisposable
{
    private const string PathToServices = "/api/v1/services.json";
    private static readonly DateTimeOffset Now = new(2026, 1, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Boot = new("a53130e9-aba0-4468-9320-34151e87a91b");
    private static readonly int[] Loads = [0, 0, 0];
    private readonly DirectoryInfo _temporary = Directory.CreateTempSubdirectory("pulse-services-tests-");
    private string DatabasePath => Path.Combine(_temporary.FullName, "pulse.db");

    [Fact]
    public async Task EmptyCatalogPublishesNoServices()
    {
        var clock = new FakeTimeProvider(Now);
        await using var app = new PulseApiFactory(DatabasePath, clock: clock);
        using var client = app.CreateClient();
        await PostAsync(client, Sample(Now, postgres: Pg(Now)));
        var snapshot = await WaitAsync(client, _ => true);
        Assert.Empty(snapshot.GetProperty("nodes").EnumerateArray());
    }

    [Fact]
    public async Task ProjectsOnlyApprovedServicesWithTypedValuesAndExplicitMissingFields()
    {
        var clock = new FakeTimeProvider(Now);
        await using var app = new PulseApiFactory(DatabasePath, clock: clock, dashboard: Catalog());
        using var client = app.CreateClient();
        var empty = await WaitAsync(client, _ => true);
        var pending = Service(empty, "postgresql", "main-db");
        Assert.Equal("unknown", pending.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, pending.GetProperty("observedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, pending.GetProperty("metrics").ValueKind);

        await PostAsync(client, Sample(Now, postgres: Pg(Now, bytes: 999999), redis: Redis(Now, used: 999999)), PulseApiFactory.OtherAgentToken);
        var queue = new
        {
            status = "available",
            observedAt = Now.UtcDateTime,
            messages = 11,
            messagesReady = 7,
            messagesUnacknowledged = 4,
            consumers = 0
        };
        var storage = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["private-sqlite"] = Storage(Now, 4096, 0, 256, 0, 4352),
            ["private-hidden"] = Storage(Now, 555555, 0, 0, 0, 555555),
        };
        await PostAsync(client, Sample(Now, postgres: Pg(Now), redis: Redis(Now, max: 0, ops: 0), rabbit: queue, sqlite: storage));
        clock.Advance(TimeSpan.FromSeconds(2));
        var snapshot = await WaitAsync(client, root => Status(root, "postgresql", "main-db") == "available");
        var pg = Metrics(snapshot, "postgresql", "main-db");
        Assert.Equal(4096, pg.GetProperty("databaseBytes").GetInt64());
        Assert.Equal(4, pg.GetProperty("connectedBackends").GetInt64());
        Assert.False(pg.GetProperty("inRecovery").GetBoolean());
        Assert.Equal(JsonValueKind.Null, pg.GetProperty("transactionsPerSecond").ValueKind);
        var redis = Metrics(snapshot, "redis", "main-cache");
        Assert.Equal(500, redis.GetProperty("usedMemoryBytes").GetInt64());
        Assert.Equal(0, redis.GetProperty("maxMemoryBytes").GetInt64());
        Assert.Equal(0, redis.GetProperty("operationsPerSecond").GetInt64());
        Assert.Equal(JsonValueKind.Null, redis.GetProperty("memoryUtilizationPercent").ValueKind);
        Assert.Equal(JsonValueKind.Null, redis.GetProperty("aofCurrentSizeBytes").ValueKind);
        var rabbit = Metrics(snapshot, "rabbitmq", "public-queue");
        Assert.Equal(11, rabbit.GetProperty("messages").GetInt64());
        Assert.Equal(7, rabbit.GetProperty("messagesReady").GetInt64());
        Assert.Equal(4, rabbit.GetProperty("messagesUnacknowledged").GetInt64());
        Assert.Equal(0, rabbit.GetProperty("consumers").GetInt64());
        var sqlite = Metrics(snapshot, "sqlite", "public-file");
        Assert.Equal(4352, sqlite.GetProperty("totalBytes").GetInt64());
        Assert.Equal(0, sqlite.GetProperty("walBytes").GetInt64());
        Assert.Equal(256, sqlite.GetProperty("sharedMemoryBytes").GetInt64());
        Assert.Single(snapshot.GetProperty("nodes").EnumerateArray());
        Assert.Equal("public-edge", snapshot.GetProperty("nodes")[0].GetProperty("alias").GetString());

        foreach (var privateValue in new[] { "demo-a", "demo-b", "private-", "bootId", "sampleId", "statsResetAt", "555555", "999999" })
        {
            Assert.DoesNotContain(privateValue, snapshot.GetRawText(), StringComparison.Ordinal);
        }

        using var response = await client.GetAsync(PathToServices);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(snapshot.GetRawText(), await File.ReadAllTextAsync(DatabasePath + ".services.json"));
    }

    [Fact]
    public async Task RatesUseDistinctCollectorTimesAndIgnoreCachedReplaysAndOtherAgents()
    {
        var clock = new FakeTimeProvider(Now);
        await using var app = new PulseApiFactory(DatabasePath, clock: clock, dashboard: Catalog());
        using var client = app.CreateClient();
        await WaitAsync(client, _ => true);
        var first = Now.AddSeconds(-60);
        var last = Now.AddSeconds(-30);
        var pgCurrent = Pg(last, commits: 140, rollbacks: 25, hits: 104, reads: 26, deadlocks: 3);
        var redisCurrent = Redis(last, hits: 57, misses: 18, evictions: 6);
        var id = Guid.NewGuid();
        await PostAsync(client, Sample(Now.AddSeconds(-4), postgres: pgCurrent, redis: redisCurrent, id: id));
        await PostAsync(client, Sample(Now.AddSeconds(-1), postgres: Pg(Now.AddSeconds(-1), commits: 999), id: id));
        await PostAsync(client, Sample(Now.AddSeconds(-2), postgres: pgCurrent, redis: redisCurrent));
        await PostAsync(client, Sample(Now.AddSeconds(-1), postgres: Pg(first.AddSeconds(-15))));
        await PostAsync(client, Sample(Now.AddSeconds(-40), postgres: Pg(first), redis: Redis(first)));
        await PostAsync(client, Sample(Now, postgres: Pg(Now, commits: 900), redis: Redis(Now, hits: 900)), PulseApiFactory.OtherAgentToken);
        clock.Advance(TimeSpan.FromSeconds(2));

        var result = await WaitAsync(client, root => At(root, "postgresql", "main-db") == last);
        var pg = Metrics(result, "postgresql", "main-db");
        Assert.Equal(30, pg.GetProperty("rateWindowSeconds").GetDouble());
        Assert.Equal(1.5, pg.GetProperty("transactionsPerSecond").GetDouble());
        Assert.Equal(80, pg.GetProperty("bufferHitPercent").GetDouble());
        Assert.Equal(1, pg.GetProperty("deadlocksDelta").GetInt64());
        var redis = Metrics(result, "redis", "main-cache");
        Assert.Equal(last, At(result, "redis", "main-cache"));
        Assert.Equal(80, redis.GetProperty("keyspaceHitPercent").GetDouble());
        Assert.Equal(2, redis.GetProperty("evictedKeysDelta").GetInt64());
        Assert.Equal(50, redis.GetProperty("memoryUtilizationPercent").GetDouble());
    }

    [Fact]
    public async Task RateWindowsBreakForCounterResetRoleChangeProbeFailureAndHostRestart()
    {
        var clock = new FakeTimeProvider(Now);
        await using var app = new PulseApiFactory(DatabasePath, clock: clock, dashboard: Catalog());
        using var client = app.CreateClient();
        await WaitAsync(client, _ => true);
        var first = Now.AddSeconds(-60);
        var next = Now.AddSeconds(-30);
        await PostAsync(client, Sample(first, postgres: Pg(first), redis: Redis(first)));
        await PostAsync(client, Sample(next, postgres: Pg(next, commits: 140), redis: Redis(next, hits: 57)));
        clock.Advance(TimeSpan.FromSeconds(2));

        var ready = await WaitAsync(client, root => At(root, "postgresql", "main-db") == next);
        Assert.Equal(JsonValueKind.Number, Metrics(ready, "postgresql", "main-db").GetProperty("transactionsPerSecond").ValueKind);
        await PostAsync(client, Sample(Now, postgres: Pg(Now, commits: 200, reset: Now.AddSeconds(-10)),
            redis: Redis(Now, hits: 5, misses: 2, evictions: 0, ops: 12)));
        clock.Advance(TimeSpan.FromSeconds(2));
        var reset = await WaitAsync(client, root => At(root, "postgresql", "main-db") == Now);
        AssertNullRate(reset, "postgresql", "main-db", "transactionsPerSecond");
        AssertNullRate(reset, "redis", "main-cache", "keyspaceHitPercent");
        Assert.Equal(12, Metrics(reset, "redis", "main-cache").GetProperty("operationsPerSecond").GetInt64());

        var renewed = Now.AddSeconds(20);
        clock.Advance(TimeSpan.FromSeconds(18));
        await PostAsync(client, Sample(renewed, postgres: Pg(renewed, commits: 240, reset: Now.AddSeconds(-10)),
            redis: new { status = "unavailable", observedAt = renewed.UtcDateTime }));
        clock.Advance(TimeSpan.FromSeconds(2));
        var afterReset = await WaitAsync(client, root => At(root, "postgresql", "main-db") == renewed);
        Assert.Equal(2, Metrics(afterReset, "postgresql", "main-db").GetProperty("transactionsPerSecond").GetDouble());
        Assert.Equal(JsonValueKind.Null, Metrics(afterReset, "postgresql", "main-db").GetProperty("bufferHitPercent").ValueKind);
        Assert.Equal("unavailable", Status(afterReset, "redis", "main-cache"));

        var moved = Now.AddSeconds(30);
        clock.Advance(TimeSpan.FromSeconds(8));
        await PostAsync(client, Sample(moved, postgres: Pg(moved, commits: 260, reset: Now.AddSeconds(-10), recovery: true),
            redis: Redis(moved, hits: 61)));
        clock.Advance(TimeSpan.FromSeconds(2));
        var relocated = await WaitAsync(client, root => At(root, "postgresql", "main-db") == moved);
        Assert.True(Metrics(relocated, "postgresql", "main-db").GetProperty("inRecovery").GetBoolean());
        AssertNullRate(relocated, "postgresql", "main-db", "transactionsPerSecond");
        AssertNullRate(relocated, "redis", "main-cache", "keyspaceHitPercent");

        var restarted = Now.AddSeconds(40);
        var newBoot = Guid.NewGuid();
        clock.Advance(TimeSpan.FromSeconds(8));
        await PostAsync(client, Sample(restarted, postgres: Pg(restarted, commits: 280, reset: Now.AddSeconds(-10), recovery: true),
            redis: Redis(restarted, hits: 80), boot: newBoot));
        clock.Advance(TimeSpan.FromSeconds(2));
        var afterRestart = await WaitAsync(client, root => At(root, "postgresql", "main-db") == restarted);
        AssertNullRate(afterRestart, "postgresql", "main-db", "transactionsPerSecond");
        AssertNullRate(afterRestart, "redis", "main-cache", "keyspaceHitPercent");

        var decreased = clock.GetUtcNow();
        await PostAsync(client, Sample(decreased, postgres: Pg(decreased, commits: 10, reset: Now.AddSeconds(-10), recovery: true),
            boot: newBoot));
        clock.Advance(TimeSpan.FromSeconds(2));
        var afterDecrease = await WaitAsync(client, root => At(root, "postgresql", "main-db") == decreased);
        AssertNullRate(afterDecrease, "postgresql", "main-db", "transactionsPerSecond");
        AssertNullRate(afterDecrease, "postgresql", "main-db", "deadlocksDelta");
    }

    [Fact]
    public async Task FreshnessComesFromTheProbeAndLateHostSamplesCannotRefreshOldReadings()
    {
        var clock = new FakeTimeProvider(Now);
        await using var app = new PulseApiFactory(DatabasePath, clock: clock, dashboard: Catalog());
        using var client = app.CreateClient();
        await WaitAsync(client, _ => true);
        var observed = Now.AddSeconds(-20);
        var failed = Now.AddSeconds(-5);
        var failure = new { status = "unavailable", observedAt = failed.UtcDateTime };
        await PostAsync(client, Sample(Now, postgres: Pg(observed), redis: failure,
            rabbit: new { status = "unavailable", observedAt = Now.AddSeconds(1).UtcDateTime }));
        clock.Advance(TimeSpan.FromSeconds(2));
        var recent = await WaitAsync(client, root => Status(root, "postgresql", "main-db") == "available");
        Assert.Equal("unavailable", Status(recent, "redis", "main-cache"));
        Assert.Equal(failed, At(recent, "redis", "main-cache"));
        Assert.Equal("unknown", Status(recent, "rabbitmq", "public-queue"));

        clock.Advance(TimeSpan.FromSeconds(66));
        await PostAsync(client, Sample(clock.GetUtcNow(), postgres: Pg(observed), redis: failure));
        clock.Advance(TimeSpan.FromSeconds(2));
        var stale = await WaitAsync(client, root => Status(root, "postgresql", "main-db") == "stale");
        Assert.Equal(observed, At(stale, "postgresql", "main-db"));
        Assert.Equal(JsonValueKind.Null, Metrics(stale, "postgresql", "main-db").ValueKind);
        Assert.Equal("unavailable", Status(stale, "redis", "main-cache"));

        clock.Advance(TimeSpan.FromSeconds(16));
        var allStale = await WaitAsync(client, root => Status(root, "redis", "main-cache") == "stale");
        Assert.Equal(JsonValueKind.Null, Metrics(allStale, "redis", "main-cache").ValueKind);
    }

    [Fact]
    public async Task ServiceViewRetriesAnAtomicReplacementFailureWithoutServingUncheckedContent()
    {
        var clock = new FakeTimeProvider(Now);
        var path = DatabasePath + ".services.json";
        Directory.CreateDirectory(path);
        await using var app = new PulseApiFactory(DatabasePath, clock: clock, dashboard: Catalog());
        using var client = app.CreateClient();
        using (var unavailable = await client.GetAsync(PathToServices))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            Assert.True(unavailable.Headers.CacheControl?.NoStore);
            Assert.DoesNotContain(path, await unavailable.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        Directory.Delete(path);
        clock.Advance(TimeSpan.FromSeconds(2));
        var published = await WaitAsync(client, _ => true);
        Assert.Equal(published.GetRawText(), await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("main-cache:Node", "private-nonexistent")]
    [InlineData("replica-name:Kind", "postgresql")]
    public async Task InvalidOrDuplicateSourceFailsStartupWithoutEchoingPrivateConfiguration(string key, string value)
    {
        var catalog = Catalog();
        catalog["Dashboard:PublicServices:" + key] = value;
        catalog["Dashboard:PublicServices:replica-name:Node"] = "public-edge";
        await using var app = new PulseApiFactory(DatabasePath, dashboard: catalog);
        var error = Assert.Throws<InvalidOperationException>(() => app.CreateClient());
        Assert.StartsWith("Dashboard services require", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-", error.Message, StringComparison.Ordinal);
    }

    public void Dispose() => _temporary.Delete(recursive: true);

    private static Dictionary<string, string?> Catalog() => new(StringComparer.Ordinal)
    {
        ["Dashboard:PublicNodes:demo-a:Alias"] = "public-edge",
        ["Dashboard:PublicServices:main-db:Node"] = "public-edge",
        ["Dashboard:PublicServices:main-db:Kind"] = "postgresql",
        ["Dashboard:PublicServices:main-cache:Node"] = "public-edge",
        ["Dashboard:PublicServices:main-cache:Kind"] = "redis",
        ["Dashboard:PublicServices:public-queue:Node"] = "public-edge",
        ["Dashboard:PublicServices:public-queue:Kind"] = "rabbitmq",
        ["Dashboard:PublicServices:public-file:Node"] = "public-edge",
        ["Dashboard:PublicServices:public-file:Kind"] = "sqlite",
        ["Dashboard:PublicServices:public-file:Source"] = "private-sqlite",
    };

    private static JsonElement Service(JsonElement root, string kind, string alias) =>
        root.GetProperty("nodes")[0].GetProperty(kind).EnumerateArray().Single(item => item.GetProperty("alias").GetString() == alias);

    private static JsonElement Metrics(JsonElement root, string kind, string alias) => Service(root, kind, alias).GetProperty("metrics");
    private static string? Status(JsonElement root, string kind, string alias) => Service(root, kind, alias).GetProperty("status").GetString();

    private static DateTimeOffset? At(JsonElement root, string kind, string alias)
    {
        var value = Service(root, kind, alias).GetProperty("observedAt");
        return value.ValueKind == JsonValueKind.String ? value.GetDateTimeOffset() : null;
    }

    private static void AssertNullRate(JsonElement result, string kind, string alias, string metric) =>
        Assert.Equal(JsonValueKind.Null, Metrics(result, kind, alias).GetProperty(metric).ValueKind);

    private static async Task<JsonElement> WaitAsync(HttpClient client, Func<JsonElement, bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            using var response = await client.GetAsync(PathToServices, timeout.Token);
            if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
            {
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                if (condition(json.RootElement))
                {
                    Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
                    Assert.Equal(TimeSpan.Zero, json.RootElement.GetProperty("generatedAt").GetDateTimeOffset().Offset);
                    return json.RootElement.Clone();
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }
    }

    private static async Task PostAsync(HttpClient client, string sample, string? token = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/telemetry");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? PulseApiFactory.AgentToken);
        request.Content = new StringContent(sample, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static object Pg(DateTimeOffset observed, long commits = 110, long rollbacks = 10, long hits = 80,
        long reads = 20, long deadlocks = 2, DateTimeOffset? reset = null, bool recovery = false, long bytes = 4096) => new
        {
            status = "available",
            observedAt = observed.UtcDateTime,
            statsResetAt = (reset ?? Now.AddHours(-1)).UtcDateTime,
            inRecovery = recovery,
            databaseBytes = bytes,
            connectedBackends = 4,
            commitsTotal = commits,
            rollbacksTotal = rollbacks,
            bufferHitsTotal = hits,
            blocksReadTotal = reads,
            deadlocksTotal = deadlocks,
        };

    private static object Redis(DateTimeOffset observed, long hits = 45, long misses = 15, long evictions = 4,
        long used = 500, long max = 1000, long ops = 4) => new
        {
            status = "available",
            observedAt = observed.UtcDateTime,
            usedMemoryBytes = used,
            maxMemoryBytes = max,
            operationsPerSecond = ops,
            connectedClients = 0,
            keyspaceHitsTotal = hits,
            keyspaceMissesTotal = misses,
            evictedKeysTotal = evictions,
        };

    private static object Storage(DateTimeOffset observed, long database, long wal, long shared, long rollback, long total) => new
    {
        status = "available",
        observedAt = observed.UtcDateTime,
        databaseBytes = database,
        walBytes = wal,
        sharedMemoryBytes = shared,
        rollbackJournalBytes = rollback,
        totalBytes = total,
    };

    private static string Sample(DateTimeOffset envelope, object? postgres = null, object? redis = null, object? rabbit = null,
        object? sqlite = null, Guid? id = null, Guid? boot = null, IReadOnlyDictionary<string, object>? additional = null)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["bootId"] = boot ?? Boot,
            ["cpuTimesSeconds"] = new { user = 0, nice = 0, system = 0, idle = 0, iowait = 0, irq = 0, softirq = 0, steal = 0 },
            ["memory"] = new { totalBytes = 1000, availableBytes = 250, swapTotalBytes = 0, swapFreeBytes = 0 },
            ["loadAverage"] = Loads,
            ["uptimeSeconds"] = 100,
            ["filesystems"] = new { },
            ["network"] = new { },
            ["temperaturesCelsius"] = new { },
        };
        foreach (var (name, reading) in new[] { ("postgresql", postgres), ("redis", redis), ("rabbitmq", rabbit), ("sqliteStorage", sqlite) })
        {
            if (reading is not null)
            {
                values.Add(name, reading);
            }
        }

        if (additional is not null)
        {
            foreach (var (name, reading) in additional)
            {
                values.Add(name, reading);
            }
        }

        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            sampleId = id ?? Guid.NewGuid(),
            observedAt = envelope.UtcDateTime,
            kind = "host",
            values,
        }, JsonSerializerOptions.Web);
    }
}
