using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace HomeLab.Pulse.Api.IntegrationTests;

public sealed class MachineTests : IDisposable
{
    private const string MachineRoot = "/api/v1/machines/";
    private const string Profile = """
        {
          "machine":{"manufacturer":"Demo Vendor","model":"Demo Host"},
          "cpu":{"architecture":"x86_64","model":"Demo CPU @ 2.6GHz","physicalCoreCount":2,"logicalCpuCount":4},
          "memory":{"osVisibleBytes":8589934592,"modules":[{"capacityBytes":8589934592,"type":"DDR4","configuredSpeedMtPerSecond":2666}]},
          "disks":{
            "private-block":{"status":"available","capacityBytes":1000000000,"model":"Demo Drive","rotational":false},
            "unapproved-drive":{"status":"available","capacityBytes":2000000000,"model":"Private Model"}
          }
        }
        """;
    private static readonly DateTimeOffset Now = new(2026, 1, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid BootId = new("3c385f3b-2739-4f7d-b12a-69f4f01c165e");
    private static readonly double[] Load = [0.5, 0.1, 0.02];
    private readonly DirectoryInfo _temporary = Directory.CreateTempSubdirectory("pulse-machine-tests-");
    private string DatabasePath => Path.Combine(_temporary.FullName, "pulse.db");
    private string SnapshotPath => DatabasePath + ".machines.json";
    private string ProfilePath => Path.Combine(_temporary.FullName, "approved.json");

    [Fact]
    public async Task PublishesApprovedInventoryAndReadoutsWithPrivateFieldsExcluded()
    {
        await File.WriteAllTextAsync(ProfilePath, Profile);
        var clock = new FakeTimeProvider(Now);
        await using var app = new PulseApiFactory(DatabasePath, clock: clock, dashboard: Catalog(ProfilePath));
        using var client = app.CreateClient();
        var empty = await WaitAsync(client, root => State(root.GetProperty("host")) == "unavailable");
        var hardware = empty.GetProperty("hardware");
        Assert.Equal("Demo Host", hardware.GetProperty("model").GetString());
        Assert.Equal("Demo CPU @ 2.6GHz", hardware.GetProperty("cpu").GetProperty("model").GetString());
        Assert.Equal(4, hardware.GetProperty("cpu").GetProperty("logicalCpuCount").GetInt64());
        Assert.Equal(2666, hardware.GetProperty("memoryModules")[0].GetProperty("configuredSpeedMtPerSecond").GetInt64());
        Assert.Equal(JsonValueKind.Null, hardware.GetProperty("memoryModules")[0].GetProperty("ratedSpeedMtPerSecond").ValueKind);

        var initialObservation = Now.AddSeconds(-14);
        var diskObservation = Now.AddSeconds(-46);
        await PostAsync(client, Sample(initialObservation, diskObservation: diskObservation));
        clock.Advance(TimeSpan.FromSeconds(2));
        var warm = await WaitAsync(client, machine => State(machine.GetProperty("host")) == "available");
        Assert.Equal("warm", warm.GetProperty("host").GetProperty("cpuRateStatus").GetString());
        Assert.Equal(JsonValueKind.Null, warm.GetProperty("host").GetProperty("metrics").GetProperty("cpuUsagePercent").ValueKind);
        Assert.Equal("warm", warm.GetProperty("host").GetProperty("networkRateStatus").GetString());

        var newObservation = Now.AddSeconds(2);
        await PostAsync(client, Sample(newObservation, user: 14, idle: 36, rx: 1800, diskObservation: diskObservation));
        clock.Advance(TimeSpan.FromSeconds(2));
        var live = await WaitAsync(client, machine => machine.GetProperty("host").GetProperty("cpuRateStatus").GetString() == "available");
        var host = live.GetProperty("host");
        Assert.Equal(newObservation, host.GetProperty("observedAt").GetDateTimeOffset());
        Assert.Equal(40, host.GetProperty("metrics").GetProperty("cpuUsagePercent").GetDouble());
        Assert.Equal(100, host.GetProperty("metrics").GetProperty("networkRxBytesPerSecond").GetDouble());
        Assert.Equal(900, host.GetProperty("uptimeSeconds").GetDouble());
        Assert.Equal(0.5, host.GetProperty("loadAverage").GetProperty("oneMinute").GetDouble());
        var filesystem = ByAlias(live, "filesystems", "root");
        Assert.Equal(120, filesystem.GetProperty("availableBytes").GetInt64());
        Assert.Equal(newObservation, filesystem.GetProperty("observedAt").GetDateTimeOffset());
        Assert.Equal(40, ByAlias(live, "temperatures", "cpu-sensor").GetProperty("celsius").GetDouble());
        Assert.Equal(0, ByAlias(live, "fans", "cpu-fan").GetProperty("rpm").GetInt64());
        var missing = ByAlias(live, "fans", "intake");
        Assert.Equal("unavailable", State(missing));
        Assert.Equal(JsonValueKind.Null, missing.GetProperty("rpm").ValueKind);

        var disk = ByAlias(live, "disks", "system-drive");
        Assert.Equal("Demo Drive", disk.GetProperty("hardware").GetProperty("model").GetString());
        var smart = disk.GetProperty("smart");
        Assert.Equal("available", State(smart));
        Assert.Equal(diskObservation, smart.GetProperty("observedAt").GetDateTimeOffset());
        Assert.Equal(0, smart.GetProperty("nvmeMediaErrors").GetInt64());
        Assert.Equal(JsonValueKind.Null, smart.GetProperty("pendingSectors").ValueKind);

        var failingAt = Now.AddSeconds(4);
        await PostAsync(client, Sample(failingAt, diskObservation: failingAt)
            .Replace("\"healthPassed\":true", "\"healthPassed\":false", StringComparison.Ordinal));
        clock.Advance(TimeSpan.FromSeconds(2));
        var unhealthy = await WaitAsync(client,
            machine => machine.GetProperty("host").GetProperty("observedAt").GetDateTimeOffset() == failingAt);
        Assert.Equal("smart.drive_needs_checkup", unhealthy.GetProperty("signals")[0].GetProperty("messageKey").GetString());

        foreach (var marker in new[] { "private-", "unapproved-drive", "Private Model", "ignored-metric", "bootId", "sampleId", "demo-a" })
        {
            Assert.DoesNotContain(marker, live.GetRawText(), StringComparison.Ordinal);
        }

        using var response = await client.GetAsync(MachineRoot + "public-edge.json");
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var fromFile = JsonDocument.Parse(await File.ReadAllTextAsync(SnapshotPath));
        Assert.Equal(fromFile.RootElement[0].GetRawText(), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SmartUsesItsOwnObservationTimeAndStopsShowingStaleValues()
    {
        var clock = new FakeTimeProvider(Now);
        await using var app = new PulseApiFactory(DatabasePath, clock: clock, dashboard: Catalog());
        using var client = app.CreateClient();
        await WaitAsync(client, machine => State(machine.GetProperty("host")) == "unavailable");
        var latestHost = Now.AddSeconds(-5);
        var latestSmart = Now.AddSeconds(-25);
        await PostAsync(client, Sample(latestHost, diskObservation: Now.AddSeconds(-39), diskTemperature: 32));
        var sampleId = Guid.NewGuid();
        await PostAsync(client, Sample(Now.AddSeconds(-20), id: sampleId, diskObservation: latestSmart, diskTemperature: 38));
        await PostAsync(client, Sample(Now, id: sampleId, diskObservation: Now.AddSeconds(-1), diskTemperature: 99));
        await PostAsync(client, Sample(Now.AddSeconds(-2), diskObservation: Now.AddSeconds(1), diskTemperature: 98));
        clock.Advance(TimeSpan.FromSeconds(2));
        var live = await WaitAsync(client, machine => State(ByAlias(machine, "disks", "system-drive").GetProperty("smart")) == "available");
        var smart = ByAlias(live, "disks", "system-drive").GetProperty("smart");
        Assert.Equal(latestSmart, smart.GetProperty("observedAt").GetDateTimeOffset());
        Assert.Equal(38, smart.GetProperty("temperatureCelsius").GetInt64());
        Assert.Equal(Now.AddSeconds(-2), live.GetProperty("host").GetProperty("observedAt").GetDateTimeOffset());

        clock.Advance(TimeSpan.FromSeconds(86));
        var staleHost = await WaitAsync(client, machine => State(machine.GetProperty("host")) == "stale");
        Assert.Equal("stale", State(ByAlias(staleHost, "filesystems", "root")));
        Assert.Equal(JsonValueKind.Null, ByAlias(staleHost, "filesystems", "root").GetProperty("totalBytes").ValueKind);
        Assert.Equal("unavailable", State(ByAlias(staleHost, "fans", "intake")));
        Assert.Equal("available", State(ByAlias(staleHost, "disks", "system-drive").GetProperty("smart")));

        clock.Advance(TimeSpan.FromSeconds(67));
        var stale = await WaitAsync(client, machine => State(ByAlias(machine, "disks", "system-drive").GetProperty("smart")) == "stale");
        smart = ByAlias(stale, "disks", "system-drive").GetProperty("smart");
        Assert.Equal(latestSmart, smart.GetProperty("observedAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, smart.GetProperty("temperatureCelsius").ValueKind);
        Assert.Equal(JsonValueKind.Null, smart.GetProperty("healthPassed").ValueKind);

        await PostAsync(client, Sample(clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromSeconds(2));
        var failure = await WaitAsync(client, machine => State(ByAlias(machine, "disks", "system-drive").GetProperty("smart")) == "unavailable");
        smart = ByAlias(failure, "disks", "system-drive").GetProperty("smart");
        Assert.Equal(JsonValueKind.Null, smart.GetProperty("observedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, smart.GetProperty("temperatureCelsius").ValueKind);
    }

    [Fact]
    public async Task RetiredAndUnknownAliasesAreNeverServedAfterRestart()
    {
        var catalog = Catalog();
        var clock = new FakeTimeProvider(Now);
        await using (var app = new PulseApiFactory(DatabasePath, clock: clock, dashboard: catalog))
        {
            using var client = app.CreateClient();
            var snapshot = await WaitAsync(client, _ => true);
            Assert.Equal(JsonValueKind.Null, snapshot.GetProperty("hardware").ValueKind);
        }

        catalog["Dashboard:PublicNodes:demo-a:Alias"] = "new-edge";
        await using var restarted = new PulseApiFactory(DatabasePath, clock: clock, dashboard: catalog);
        using var restartedClient = restarted.CreateClient();
        foreach (var alias in new[] { "public-edge", "demo-a", "NEW-EDGE", "absent", "wrong.suffix" })
        {
            using var response = await restartedClient.GetAsync(MachineRoot + alias + ".json");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.DoesNotContain(alias, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        var current = await WaitAsync(restartedClient, _ => true, alias: "new-edge");
        Assert.Equal("new-edge", current.GetProperty("alias").GetString());
        Assert.DoesNotContain("public-edge", await File.ReadAllTextAsync(SnapshotPath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"model\":\"Demo Host\"", "\"serialNumber\":\"private-value\"")]
    [InlineData("\"Demo Host\"", "\"Demo 192.0.2.8\"")]
    [InlineData("\"Demo Host\"", "\"/private/model\"")]
    [InlineData("\"Demo Host\"", "\"Demo Host\",\"model\":\"Second label\"")]
    public async Task InvalidOrUnsafeHardwareFailsStartupWithASafeError(string original, string edited)
    {
        await File.WriteAllTextAsync(ProfilePath, Profile.Replace(original, edited, StringComparison.Ordinal));
        await using var app = new PulseApiFactory(DatabasePath, dashboard: Catalog(ProfilePath));
        var failure = Assert.Throws<InvalidOperationException>(() => app.CreateClient());
        Assert.StartsWith("Dashboard hardware inventory", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ProfilePath, failure.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(edited, failure.ToString(), StringComparison.Ordinal);
    }

    public void Dispose() => _temporary.Delete(recursive: true);

    private static Dictionary<string, string?> Catalog(string? hardware = null)
    {
        var catalog = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Dashboard:PublicNodes:demo-a:Alias"] = "public-edge",
            ["Dashboard:PublicNodes:demo-a:Metrics:0"] = "cpu",
            ["Dashboard:PublicNodes:demo-a:Metrics:1"] = "network",
            ["Dashboard:PublicNodes:demo-a:NetworkInterface"] = "private-nic",
            ["Dashboard:PublicNodes:demo-a:Machine:IncludeUptime"] = "true",
            ["Dashboard:PublicNodes:demo-a:Machine:IncludeLoad"] = "true",
            ["Dashboard:PublicNodes:demo-a:Machine:Filesystems:root"] = "private-fs",
            ["Dashboard:PublicNodes:demo-a:Machine:Temperatures:cpu-sensor"] = "private-temp",
            ["Dashboard:PublicNodes:demo-a:Machine:Fans:cpu-fan"] = "private-fan",
            ["Dashboard:PublicNodes:demo-a:Machine:Fans:intake"] = "missing-fan",
            ["Dashboard:PublicNodes:demo-a:Machine:Disks:system-drive"] = "private-block",
        };
        if (hardware is not null)
        {
            catalog["Dashboard:PublicNodes:demo-a:Machine:HardwareProfilePath"] = hardware;
        }

        return catalog;
    }

    private static JsonElement ByAlias(JsonElement root, string key, string alias) =>
        root.GetProperty(key).EnumerateArray().Single(item => item.GetProperty("alias").GetString() == alias);

    private static string? State(JsonElement element) => element.GetProperty("status").GetString();

    private static async Task<JsonElement> WaitAsync(HttpClient client, Func<JsonElement, bool> condition, string alias = "public-edge")
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            using var response = await client.GetAsync(MachineRoot + alias + ".json", timeout.Token);
            if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
            {
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                var machine = json.RootElement.GetProperty("machine");
                if (condition(machine))
                {
                    Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
                    Assert.Equal(TimeSpan.Zero, json.RootElement.GetProperty("generatedAt").GetDateTimeOffset().Offset);
                    return machine.Clone();
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }
    }

    private static async Task PostAsync(HttpClient client, string sample)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/telemetry");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", PulseApiFactory.AgentToken);
        request.Content = new StringContent(sample, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static string Sample(DateTimeOffset observed, int user = 10, int idle = 30, int rx = 200, Guid? id = null,
        DateTimeOffset? diskObservation = null, int diskTemperature = 33) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            sampleId = id ?? Guid.NewGuid(),
            observedAt = observed.UtcDateTime,
            kind = "host",
            values = new
            {
                bootId = BootId,
                cpuTimesSeconds = new { user, nice = 0, system = 0, idle, iowait = 0, irq = 0, softirq = 0, steal = 0 },
                memory = new { totalBytes = 1000, availableBytes = 250, swapTotalBytes = 0, swapFreeBytes = 0 },
                loadAverage = Load,
                uptimeSeconds = 900,
                filesystems = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["private-fs"] = new { totalBytes = 500, availableBytes = 120 },
                    ["ignored-metric"] = new { totalBytes = 800, availableBytes = 300 },
                },
                network = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["private-nic"] = new { rxBytes = rx, txBytes = 0 },
                },
                temperaturesCelsius = new Dictionary<string, double>(StringComparer.Ordinal) { ["private-temp"] = 40, ["ignored-metric"] = 99 },
                fanSpeedsRpm = new Dictionary<string, int>(StringComparer.Ordinal) { ["private-fan"] = 0 },
                diskSmart = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["private-block"] = diskObservation is null ? new { status = "unavailable" } : new
                    {
                        status = "available",
                        observedAt = diskObservation.Value.UtcDateTime,
                        temperatureCelsius = diskTemperature,
                        healthPassed = true,
                        nvmeMediaErrors = 0,
                    },
                },
            },
        }, JsonSerializerOptions.Web);
}
