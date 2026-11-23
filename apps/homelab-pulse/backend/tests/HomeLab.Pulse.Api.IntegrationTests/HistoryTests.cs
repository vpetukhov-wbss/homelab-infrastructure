using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace HomeLab.Pulse.Api.IntegrationTests;

public sealed class HistoryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 27, 12, 3, 0, TimeSpan.Zero);
    private static readonly Guid BootA = new("8b778eef-b27c-49c3-bdf0-63b35551572d");
    private static readonly Guid BootB = new("31e1238c-d6cb-43af-aa2d-cb73485c3a28");
    private static readonly double[] LoadAverage = [0.1, 0.1, 0.1];
    private readonly DirectoryInfo _temporary = Directory.CreateTempSubdirectory("pulse-history-tests-");
    private string DatabasePath => Path.Combine(_temporary.FullName, "pulse.db");

    [Fact]
    public async Task PublishesFixedUtcWindowsAndWeightedApprovedFleetMetrics()
    {
        var clock = new FakeTimeProvider(Now);
        await using var app = new PulseApiFactory(DatabasePath, clock: clock, dashboard: Catalog());
        using var client = app.CreateClient();
        await WaitForHistoryAsync(client, "1h");

        await PostAsync(client, PulseApiFactory.AgentToken, new SampleInput
        {
            ObservedAt = Now.AddSeconds(-45),
            BootId = BootA,
            BusySeconds = 10,
            IdleSeconds = 10,
            MemoryTotalBytes = 1_000,
            MemoryAvailableBytes = 600,
        });
        await PostAsync(client, PulseApiFactory.AgentToken, new SampleInput
        {
            ObservedAt = Now.AddSeconds(-30),
            BootId = BootA,
            BusySeconds = 20,
            IdleSeconds = 20,
            MemoryTotalBytes = 1_000,
            MemoryAvailableBytes = 500,
        });
        await PostAsync(client, PulseApiFactory.OtherAgentToken, new SampleInput
        {
            ObservedAt = Now.AddSeconds(-45),
            BootId = BootB,
            BusySeconds = 0,
            IdleSeconds = 10,
            MemoryTotalBytes = 3_000,
            MemoryAvailableBytes = 2_000,
        });
        await PostAsync(client, PulseApiFactory.OtherAgentToken, new SampleInput
        {
            ObservedAt = Now.AddSeconds(-30),
            BootId = BootB,
            BusySeconds = 30,
            IdleSeconds = 20,
            MemoryTotalBytes = 3_000,
            MemoryAvailableBytes = 1_500,
        });

        clock.Advance(TimeSpan.FromMinutes(5));
        var history = await WaitForHistoryAsync(client, "1h", root =>
            root.GetProperty("history").GetProperty("points").EnumerateArray().Any(point =>
                point.GetProperty("nodes").EnumerateArray().Any(node => node.GetProperty("status").GetString() == "available")));
        var payload = history.GetProperty("history");
        Assert.Equal(30, payload.GetProperty("points").GetArrayLength());
        Assert.Equal(120, payload.GetProperty("resolutionSeconds").GetInt32());
        var point = payload.GetProperty("points").EnumerateArray().Single(item =>
            item.GetProperty("cpuUsagePercent").ValueKind == JsonValueKind.Number);
        Assert.Equal(68.75, point.GetProperty("cpuUsagePercent").GetDouble(), precision: 2);
        Assert.Equal(2_000, point.GetProperty("memoryUsedBytes").GetInt64());
        Assert.Equal(4_000, point.GetProperty("memoryTotalBytes").GetInt64());
        var startedAt = point.GetProperty("startedAt").GetDateTimeOffset();
        Assert.Equal(TimeSpan.Zero, startedAt.Offset);
        Assert.Equal(0, startedAt.Minute % 2);
        Assert.Equal(0, startedAt.Second);
        Assert.Equal(TimeSpan.FromMinutes(2),
            point.GetProperty("endedAt").GetDateTimeOffset() - startedAt);
        Assert.Contains(payload.GetProperty("points").EnumerateArray(), item =>
            item.GetProperty("nodes").EnumerateArray().All(node => node.GetProperty("status").GetString() == "missing"));

        Assert.Equal(96, (await WaitForHistoryAsync(client, "24h")).GetProperty("history").GetProperty("points").GetArrayLength());
        Assert.Equal(168, (await WaitForHistoryAsync(client, "7d")).GetProperty("history").GetProperty("points").GetArrayLength());
        foreach (var marker in new[] { "demo-a", "demo-b", "private", BootA.ToString(), BootB.ToString() })
        {
            Assert.DoesNotContain(marker, history.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }

        clock.Advance(TimeSpan.FromSeconds(2));
        var machine = await WaitForMachineAsync(client);
        var machineHistory = machine.GetProperty("history");
        Assert.Equal("1h", machineHistory.GetProperty("window").GetString());
        Assert.Equal(30, machineHistory.GetProperty("points").GetArrayLength());
    }

    [Fact]
    public async Task LateArrivalFillsABucketWhileCounterResetAndGapsStayMissing()
    {
        var clock = new FakeTimeProvider(Now);
        await using var app = new PulseApiFactory(DatabasePath, clock: clock, dashboard: Catalog(singleNode: true));
        using var client = app.CreateClient();
        await WaitForHistoryAsync(client, "1h");

        await PostAsync(client, PulseApiFactory.AgentToken, new SampleInput
        {
            ObservedAt = Now.AddMinutes(-4),
            BootId = BootA,
            BusySeconds = 100,
            IdleSeconds = 100,
            MemoryTotalBytes = 1_000,
            MemoryAvailableBytes = 500,
        });
        await PostAsync(client, PulseApiFactory.AgentToken, new SampleInput
        {
            ObservedAt = Now.AddMinutes(-3).AddSeconds(30),
            BootId = BootA,
            BusySeconds = 1,
            IdleSeconds = 1,
            MemoryTotalBytes = 1_000,
            MemoryAvailableBytes = 400,
        });
        await PostAsync(client, PulseApiFactory.AgentToken, new SampleInput
        {
            ObservedAt = Now.AddMinutes(-2).AddSeconds(30),
            BootId = BootA,
            BusySeconds = 10,
            IdleSeconds = 10,
            MemoryTotalBytes = 1_000,
            MemoryAvailableBytes = 350,
        });
        await PostAsync(client, PulseApiFactory.AgentToken, new SampleInput
        {
            ObservedAt = Now.AddMinutes(-2),
            BootId = BootB,
            BusySeconds = 20,
            IdleSeconds = 20,
            MemoryTotalBytes = 1_000,
            MemoryAvailableBytes = 300,
        });

        clock.Advance(TimeSpan.FromMinutes(5));
        var refreshed = await WaitForHistoryAsync(client, "1h", root => root.GetProperty("generatedAt").GetDateTimeOffset() == clock.GetUtcNow());
        var available = refreshed.GetProperty("history").GetProperty("points").EnumerateArray()
            .Where(item => item.GetProperty("nodes")[0].GetProperty("status").GetString() == "available").ToArray();
        Assert.NotEmpty(available);
        Assert.All(available, item => Assert.Equal(JsonValueKind.Null, item.GetProperty("cpuUsagePercent").ValueKind));
        Assert.Contains(refreshed.GetProperty("history").GetProperty("points").EnumerateArray(), item =>
            item.GetProperty("nodes")[0].GetProperty("status").GetString() == "missing");
    }

    [Fact]
    public async Task RebuildsFromSqliteAfterRestartAndRejectsUnknownWindows()
    {
        var clock = new FakeTimeProvider(Now);
        await using (var app = new PulseApiFactory(DatabasePath, clock: clock, dashboard: Catalog(singleNode: true)))
        {
            using var client = app.CreateClient();
            await PostAsync(client, PulseApiFactory.AgentToken, new SampleInput
            {
                ObservedAt = Now.AddSeconds(-20),
                BootId = BootA,
                BusySeconds = 5,
                IdleSeconds = 5,
                MemoryTotalBytes = 1_000,
                MemoryAvailableBytes = 250,
            });
        }

        await using var restarted = new PulseApiFactory(DatabasePath, clock: clock, dashboard: Catalog(singleNode: true));
        using var restartedClient = restarted.CreateClient();
        var history = await WaitForHistoryAsync(restartedClient, "1h", root =>
            root.GetProperty("history").GetProperty("points").EnumerateArray().Any(item =>
                item.GetProperty("nodes")[0].GetProperty("status").GetString() == "available"));
        Assert.Contains(history.GetProperty("history").GetProperty("points").EnumerateArray(), item =>
            item.GetProperty("memoryUsedBytes").ValueKind == JsonValueKind.Number);

        using var unknown = await restartedClient.GetAsync("/api/v1/history/fleet/30d.json");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.True(unknown.Headers.CacheControl?.NoStore);
        Assert.DoesNotContain("30d", await unknown.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    public void Dispose() => _temporary.Delete(recursive: true);

    private Dictionary<string, string?> Catalog(bool singleNode = false)
    {
        var firstProfile = WriteProfile("first.json", logicalCpuCount: 2);
        var catalog = NodeCatalog("demo-a", "public-edge", firstProfile);
        if (!singleNode)
        {
            var secondProfile = WriteProfile("second.json", logicalCpuCount: 6);
            foreach (var item in NodeCatalog("demo-b", "public-data", secondProfile))
            {
                catalog.Add(item.Key, item.Value);
            }
        }

        return catalog;
    }

    private static Dictionary<string, string?> NodeCatalog(string agent, string alias, string profile) => new(StringComparer.Ordinal)
    {
        [$"Dashboard:PublicNodes:{agent}:Alias"] = alias,
        [$"Dashboard:PublicNodes:{agent}:Metrics:0"] = "cpu",
        [$"Dashboard:PublicNodes:{agent}:Metrics:1"] = "memory",
        [$"Dashboard:PublicNodes:{agent}:Machine:HardwareProfilePath"] = profile,
    };

    private string WriteProfile(string name, int logicalCpuCount)
    {
        var path = Path.Combine(_temporary.FullName, name);
        File.WriteAllText(path, $$"""
            {
              "machine":{"manufacturer":"Demo Vendor","model":"Demo Host"},
              "cpu":{"architecture":"x86_64","logicalCpuCount":{{logicalCpuCount}}},
              "memory":{"osVisibleBytes":8589934592}
            }
            """);
        return path;
    }

    private static async Task PostAsync(HttpClient client, string token, SampleInput input)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/telemetry");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(input.Json(), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<JsonElement> WaitForHistoryAsync(
        HttpClient client,
        string window,
        Func<JsonElement, bool>? condition = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            using var response = await client.GetAsync($"/api/v1/history/fleet/{window}.json", timeout.Token);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                if (condition is null || condition(document.RootElement))
                {
                    Assert.True(response.Headers.CacheControl?.NoStore);
                    Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
                    return document.RootElement.Clone();
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }
    }

    private static async Task<JsonElement> WaitForMachineAsync(HttpClient client)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            using var response = await client.GetAsync("/api/v1/machines/public-edge.json", timeout.Token);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                var machine = document.RootElement.GetProperty("machine");
                if (machine.GetProperty("history").GetProperty("points").GetArrayLength() == 30)
                {
                    return machine.Clone();
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }
    }

    private sealed record SampleInput
    {
        public required DateTimeOffset ObservedAt { get; init; }

        public required Guid BootId { get; init; }

        public required double BusySeconds { get; init; }

        public required double IdleSeconds { get; init; }

        public required long MemoryTotalBytes { get; init; }

        public required long MemoryAvailableBytes { get; init; }

        public string Json() => JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            sampleId = Guid.NewGuid(),
            observedAt = ObservedAt.UtcDateTime,
            kind = "host",
            values = new
            {
                bootId = BootId,
                cpuTimesSeconds = new
                {
                    user = BusySeconds,
                    nice = 0,
                    system = 0,
                    idle = IdleSeconds,
                    iowait = 0,
                    irq = 0,
                    softirq = 0,
                    steal = 0,
                },
                memory = new
                {
                    totalBytes = MemoryTotalBytes,
                    availableBytes = MemoryAvailableBytes,
                    swapTotalBytes = 0,
                    swapFreeBytes = 0,
                },
                loadAverage = LoadAverage,
                uptimeSeconds = 100,
                filesystems = new { },
                network = new { },
                temperaturesCelsius = new { },
            },
        }, JsonSerializerOptions.Web);
    }
}
