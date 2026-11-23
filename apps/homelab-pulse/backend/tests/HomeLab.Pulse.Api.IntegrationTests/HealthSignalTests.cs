using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Options;
using Xunit;

namespace HomeLab.Pulse.Api.IntegrationTests;

public sealed class HealthSignalTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 1, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Boot = new("61e46480-e97e-45a9-bb71-7c2a80c60b06");
    private static readonly int[] EmptyLoad = [0, 0, 0];
    private readonly DirectoryInfo _temporary = Directory.CreateTempSubdirectory("pulse-health-tests-");
    private string DatabasePath => Path.Combine(_temporary.FullName, "pulse.db");

    [Fact]
    public async Task SustainedSignalsRequireDistinctFreshObservationsAndRecoverDeterministically()
    {
        var clock = new FakeTimeProvider(Now);
        await using var app = new PulseApiFactory(DatabasePath, clock: clock, dashboard: Catalog());
        using var client = app.CreateClient();
        await WaitAsync(client, node => node.GetProperty("status").GetString() == "unavailable");

        await PostAsync(client, Sample(new SampleValues
        {
            ObservedAt = Now.AddSeconds(-10), User = 0, Idle = 10, AvailableBytes = 500, TemperatureCelsius = 40,
        }));
        await PostAsync(client, Sample(new SampleValues
        {
            ObservedAt = Now, User = 10, Idle = 10, AvailableBytes = 50, TemperatureCelsius = 90,
        }));
        clock.Advance(TimeSpan.FromSeconds(2));
        var first = await WaitAsync(client, node => node.GetProperty("status").GetString() == "available");
        Assert.Equal(["disk.getting_crowded"], Keys(first));

        for (var minute = 1; minute <= 5; minute++)
        {
            var at = Now.AddMinutes(minute);
            clock.Advance(at - clock.GetUtcNow());
            await PostAsync(client, Sample(new SampleValues
            {
                ObservedAt = at, User = 10 + (minute * 10), Idle = 10, AvailableBytes = 50, TemperatureCelsius = 90,
            }));
            clock.Advance(TimeSpan.FromSeconds(2));
        }

        var sustained = await WaitAsync(client, node => Keys(node).Contains("cpu.doing_overtime", StringComparer.Ordinal));
        Assert.Equal(["cpu.doing_overtime", "memory.getting_crowded", "temperature.getting_hot", "disk.getting_crowded"],
            Keys(sustained));
        var cpu = sustained.GetProperty("signals").EnumerateArray()
            .Single(signal => signal.GetProperty("messageKey").GetString() == "cpu.doing_overtime");
        Assert.Equal("5m", cpu.GetProperty("window").GetString());
        Assert.Equal(85, cpu.GetProperty("threshold").GetDouble());

        var recoveryAt = Now.AddMinutes(6);
        clock.Advance(recoveryAt - clock.GetUtcNow());
        await PostAsync(client, Sample(new SampleValues
        {
            ObservedAt = recoveryAt, User = 60, Idle = 20, AvailableBytes = 500, TemperatureCelsius = 40,
        }));
        clock.Advance(TimeSpan.FromSeconds(2));
        var recovered = await WaitAsync(client, node => node.GetProperty("observedAt").GetDateTimeOffset() == recoveryAt);
        Assert.Empty(Keys(recovered));

        clock.Advance(TimeSpan.FromSeconds(90));
        var stale = await WaitAsync(client, node => node.GetProperty("status").GetString() == "stale");
        Assert.Equal(["node.ghosted_us"], Keys(stale));
        Assert.Equal(90, stale.GetProperty("signals")[0].GetProperty("threshold").GetDouble());
    }

    [Fact]
    public async Task UnsafeThresholdConfigurationStopsStartup()
    {
        var catalog = Catalog();
        catalog["Dashboard:Signals:DiskUsagePercent"] = "101";
        await using var app = new PulseApiFactory(DatabasePath, dashboard: catalog);
        Assert.Throws<OptionsValidationException>(() => app.CreateClient());
    }

    public void Dispose() => _temporary.Delete(recursive: true);

    private static Dictionary<string, string?> Catalog() => new(StringComparer.Ordinal)
    {
        ["Dashboard:PublicNodes:demo-a:Alias"] = "public-edge",
        ["Dashboard:PublicNodes:demo-a:Metrics:0"] = "cpu",
        ["Dashboard:PublicNodes:demo-a:Metrics:1"] = "memory",
        ["Dashboard:PublicNodes:demo-a:Metrics:2"] = "disk",
        ["Dashboard:PublicNodes:demo-a:Metrics:3"] = "temperature",
        ["Dashboard:PublicNodes:demo-a:Filesystem"] = "root",
        ["Dashboard:PublicNodes:demo-a:TemperatureSensor"] = "cpu",
    };

    private static string[] Keys(JsonElement node) => node.GetProperty("signals").EnumerateArray()
        .Select(signal => signal.GetProperty("messageKey").GetString()!).ToArray();

    private static async Task<JsonElement> WaitAsync(HttpClient client, Func<JsonElement, bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            using var response = await client.GetAsync("/api/v1/overview.json", timeout.Token);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                var node = json.RootElement.GetProperty("nodes")[0];
                if (condition(node))
                {
                    return node.Clone();
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

    private static string Sample(SampleValues sample) => JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            sampleId = Guid.NewGuid(),
            observedAt = sample.ObservedAt.UtcDateTime,
            kind = "host",
            values = new
            {
                bootId = Boot,
                cpuTimesSeconds = new { user = sample.User, nice = 0, system = 0, idle = sample.Idle, iowait = 0, irq = 0, softirq = 0, steal = 0 },
                memory = new { totalBytes = 1000, availableBytes = sample.AvailableBytes, swapTotalBytes = 0, swapFreeBytes = 0 },
                loadAverage = EmptyLoad,
                uptimeSeconds = 1,
                filesystems = new { root = new { totalBytes = 1000, availableBytes = sample.AvailableBytes } },
                network = new { },
                temperaturesCelsius = new { cpu = sample.TemperatureCelsius },
            },
        }, JsonSerializerOptions.Web);

    private sealed record SampleValues
    {
        public required DateTimeOffset ObservedAt { get; init; }

        public required int User { get; init; }

        public required int Idle { get; init; }

        public required int AvailableBytes { get; init; }

        public required double TemperatureCelsius { get; init; }
    }
}
