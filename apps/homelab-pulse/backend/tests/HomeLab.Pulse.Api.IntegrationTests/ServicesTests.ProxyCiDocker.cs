using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace HomeLab.Pulse.Api.IntegrationTests;

public sealed partial class ServicesTests
{
    [Fact]
    public async Task ServiceSignalsFollowVerifiedMeasurementsAndClearOnRecovery()
    {
        var clock = new FakeTimeProvider(Now);
        await using var app = new PulseApiFactory(DatabasePath, clock: clock, dashboard: ProxyCatalog());
        using var client = app.CreateClient();
        await WaitAsync(client, _ => true);

        await PostAsync(client, Sample(Now, additional: HealthReadings(Now, unhealthy: true)));
        clock.Advance(TimeSpan.FromSeconds(2));
        var active = await WaitAsync(client, root => At(root, "giteaActions", "demo-ci") == Now);
        Assert.Equal("nginx.proxy_busy", SignalKey(active, "nginxAccess", "demo-access"));
        Assert.Equal("tls.expiring_soon", SignalKey(active, "tls", "demo-cert"));
        Assert.Equal("gitea.feeling_slow", SignalKey(active, "gitea", "demo-git"));
        Assert.Equal("ci.queue_growing", SignalKey(active, "giteaActions", "demo-ci"));
        Assert.Equal("docker.workload_unhealthy", SignalKey(active, "dockerWorkloads", "demo-app"));

        var recoveredAt = Now.AddSeconds(10);
        clock.Advance(TimeSpan.FromSeconds(8));
        await PostAsync(client, Sample(recoveredAt, additional: HealthReadings(recoveredAt, unhealthy: false)));
        clock.Advance(TimeSpan.FromSeconds(2));
        var recovered = await WaitAsync(client, root => At(root, "giteaActions", "demo-ci") == recoveredAt);
        foreach (var (kind, alias) in new[] { ("nginxAccess", "demo-access"), ("tls", "demo-cert"),
            ("gitea", "demo-git"), ("giteaActions", "demo-ci"), ("dockerWorkloads", "demo-app") })
        {
            Assert.Empty(Service(recovered, kind, alias).GetProperty("signals").EnumerateArray());
        }
    }

    [Fact]
    public async Task ProxyAutomationAndDockerPublishOnlyApprovedMetricsAndAliases()
    {
        var clock = new FakeTimeProvider(Now);
        await using var app = new PulseApiFactory(DatabasePath, clock: clock, dashboard: ProxyCatalog());
        using var client = app.CreateClient();
        await WaitAsync(client, _ => true);
        var at = Now.AddSeconds(-4);
        var expiry = Now.AddDays(10);
        await PostAsync(client, Sample(Now.AddSeconds(-20), additional: new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["nginx"] = Stub(Now.AddSeconds(-24), requests: 100),
        }));
        await PostAsync(client, Sample(Now, additional: new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["nginx"] = Stub(at, requests: 140),
            ["nginxAccess"] = Access(at, requests: 8, errors: 2, p95: 128),
            ["tls"] = new { status = "available", observedAt = at.UtcDateTime, expiresAt = expiry.UtcDateTime },
            ["gitea"] = new { status = "available", observedAt = at.UtcDateTime, databaseHealthy = true, cacheHealthy = false, responseTimeMs = 17 },
            ["giteaActions"] = Actions(at, timed: 2, mean: 23),
            ["dockerWorkloads"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["private-workload"] = new
                {
                    status = "available",
                    observedAt = at.UtcDateTime,
                    state = "running",
                    restartCount = 0,
                    restartPolicy = "unless-stopped",
                    memoryLimitBytes = 4096,
                    cpuLimitCores = 1.5,
                    usage = new { status = "available", cpuUsageCores = 0, memoryUsageBytes = 512 },
                    volumeAttachments = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["private-volume"] = "mixed",
                        ["secret-volume"] = "rw",
                    },
                },
                ["secret-workload"] = Workload(at, restartCount: 555555),
            },
            ["dockerVolumes"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["private-volume"] = new
                {
                    status = "available",
                    observedAt = at.UtcDateTime,
                    usedBytes = 1000,
                    filesystemCapacityBytes = 9000,
                    filesystemAvailableBytes = 0,
                },
                ["secret-volume"] = new { status = "available", observedAt = at.UtcDateTime, usedBytes = 777777 },
            },
        }));
        clock.Advance(TimeSpan.FromSeconds(2));

        var snapshot = await WaitAsync(client, root => At(root, "nginx", "demo-stub") == at);
        var stub = Metrics(snapshot, "nginx", "demo-stub");
        Assert.Equal(2, stub.GetProperty("requestsPerSecond").GetDouble());
        Assert.Equal(20, stub.GetProperty("rateWindowSeconds").GetDouble());
        var access = Metrics(snapshot, "nginxAccess", "demo-access");
        Assert.Equal(25, access.GetProperty("serverErrorPercent").GetDouble());
        Assert.Equal(128, access.GetProperty("requestTimeP95Ms").GetInt64());
        Assert.Equal("nginx.proxy_busy", Service(snapshot, "nginxAccess", "demo-access")
            .GetProperty("signals")[0].GetProperty("messageKey").GetString());
        Assert.Equal(expiry, Metrics(snapshot, "tls", "demo-cert").GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal("tls.expiring_soon", Service(snapshot, "tls", "demo-cert")
            .GetProperty("signals")[0].GetProperty("messageKey").GetString());
        Assert.False(Metrics(snapshot, "gitea", "demo-git").GetProperty("cacheHealthy").GetBoolean());
        Assert.Equal("gitea.check_failed", Service(snapshot, "gitea", "demo-git")
            .GetProperty("signals")[0].GetProperty("messageKey").GetString());
        var actions = Metrics(snapshot, "giteaActions", "demo-ci");
        Assert.Equal(0, actions.GetProperty("waitingJobs").GetInt64());
        Assert.Equal(23, actions.GetProperty("meanJobDurationSecondsLastHour").GetInt64());
        Assert.Equal(1, actions.GetProperty("activeRunners").GetInt64());
        var workload = Metrics(snapshot, "dockerWorkloads", "demo-app");
        Assert.Equal("running", workload.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, workload.GetProperty("health").ValueKind);
        Assert.Equal(0, workload.GetProperty("restartCount").GetInt64());
        Assert.Equal(0, workload.GetProperty("usage").GetProperty("cpuUsageCores").GetDouble());
        var attachment = Assert.Single(workload.GetProperty("volumeAttachments").EnumerateArray());
        Assert.Equal("demo-data", attachment.GetProperty("volumeAlias").GetString());
        Assert.Equal("mixed", attachment.GetProperty("mode").GetString());
        var volume = Metrics(snapshot, "dockerVolumes", "demo-data");
        Assert.Equal("sharedFilesystem", volume.GetProperty("capacityScope").GetString());
        Assert.Equal(1000, volume.GetProperty("usedBytes").GetInt64());
        Assert.Equal(0, volume.GetProperty("filesystemAvailableBytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, volume.GetProperty("quotaBytes").ValueKind);

        foreach (var sentinel in new[] { "private-", "secret-", "bootId", "sampleId", "555555", "777777" })
        {
            Assert.DoesNotContain(sentinel, snapshot.GetRawText(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SnapshotFreshnessUsesEachCollectorBudgetAndRetainsUntimedFailure()
    {
        var clock = new FakeTimeProvider(Now);
        await using var app = new PulseApiFactory(DatabasePath, clock: clock, dashboard: ProxyCatalog());
        using var client = app.CreateClient();
        await WaitAsync(client, _ => true);
        var recent = Now.AddSeconds(-100);
        var volumeAt = Now.AddSeconds(-800);
        await PostAsync(client, Sample(Now, additional: new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["nginx"] = Stub(recent, requests: 0),
            ["giteaActions"] = new { status = "unavailable" },
            ["dockerWorkloads"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["private-workload"] = Workload(recent),
            },
            ["dockerVolumes"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["private-volume"] = new { status = "available", observedAt = volumeAt.UtcDateTime, usedBytes = 0 },
            },
        }));
        clock.Advance(TimeSpan.FromSeconds(2));

        var initial = await WaitAsync(client, root => Status(root, "dockerWorkloads", "demo-app") == "available");
        Assert.Equal("stale", Status(initial, "nginx", "demo-stub"));
        Assert.Equal("unavailable", Status(initial, "giteaActions", "demo-ci"));
        Assert.Equal("service.check_failed", Service(initial, "giteaActions", "demo-ci")
            .GetProperty("signals")[0].GetProperty("messageKey").GetString());
        Assert.Null(At(initial, "giteaActions", "demo-ci"));
        var volume = Metrics(initial, "dockerVolumes", "demo-data");
        Assert.Equal(0, volume.GetProperty("usedBytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, volume.GetProperty("capacityScope").ValueKind);
        Assert.Equal(JsonValueKind.Null, volume.GetProperty("filesystemCapacityBytes").ValueKind);

        clock.Advance(TimeSpan.FromSeconds(78));
        var workloadStale = await WaitAsync(client, root => Status(root, "dockerWorkloads", "demo-app") == "stale");
        Assert.Equal(recent, At(workloadStale, "dockerWorkloads", "demo-app"));
        Assert.Equal("available", Status(workloadStale, "dockerVolumes", "demo-data"));

        clock.Advance(TimeSpan.FromSeconds(20));
        var volumeStale = await WaitAsync(client, root => Status(root, "dockerVolumes", "demo-data") == "stale");
        Assert.Equal(volumeAt, At(volumeStale, "dockerVolumes", "demo-data"));
        Assert.Equal(JsonValueKind.Null, Metrics(volumeStale, "dockerVolumes", "demo-data").ValueKind);

        clock.Advance(TimeSpan.FromSeconds(80));
        var ciStale = await WaitAsync(client, root => Status(root, "giteaActions", "demo-ci") == "stale");
        Assert.Null(At(ciStale, "giteaActions", "demo-ci"));
    }

    [Fact]
    public async Task TruncatedWindowsAndCounterResetsSuppressRatesWithoutReplacingZero()
    {
        var clock = new FakeTimeProvider(Now);
        await using var app = new PulseApiFactory(DatabasePath, clock: clock, dashboard: ProxyCatalog());
        using var client = app.CreateClient();
        await WaitAsync(client, _ => true);
        await PostAsync(client, Sample(Now.AddSeconds(-20), additional: new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["nginx"] = Stub(Now.AddSeconds(-21), requests: 100),
        }));
        await PostAsync(client, Sample(Now, additional: new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["nginx"] = Stub(Now, requests: 0),
            ["nginxAccess"] = Access(Now, requests: 10, errors: 2, truncated: true),
            ["giteaActions"] = Actions(Now, timed: 0),
            ["dockerWorkloads"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["private-workload"] = Workload(Now),
            },
            ["dockerVolumes"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["private-volume"] = new { status = "available", observedAt = Now.UtcDateTime, usedBytes = 2, filesystemCapacityBytes = 1, filesystemAvailableBytes = 2 },
            },
        }));
        clock.Advance(TimeSpan.FromSeconds(2));

        var reset = await WaitAsync(client, root => At(root, "nginx", "demo-stub") == Now);
        var stub = Metrics(reset, "nginx", "demo-stub");
        Assert.Equal(0, stub.GetProperty("requestsTotal").GetInt64());
        Assert.Equal(JsonValueKind.Null, stub.GetProperty("requestsPerSecond").ValueKind);
        var access = Metrics(reset, "nginxAccess", "demo-access");
        Assert.Equal(10, access.GetProperty("requests").GetInt64());
        Assert.True(access.GetProperty("tailTruncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, access.GetProperty("requestTimeP95Ms").ValueKind);
        Assert.Equal(JsonValueKind.Null, access.GetProperty("serverErrorPercent").ValueKind);
        Assert.Equal(JsonValueKind.Null, Metrics(reset, "giteaActions", "demo-ci").GetProperty("meanJobDurationSecondsLastHour").ValueKind);
        var usage = Metrics(reset, "dockerWorkloads", "demo-app").GetProperty("usage");
        Assert.Equal("unavailable", usage.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("memoryUsageBytes").ValueKind);
        var volume = Metrics(reset, "dockerVolumes", "demo-data");
        Assert.Equal(2, volume.GetProperty("usedBytes").GetInt64());
        Assert.Equal(JsonValueKind.Null, volume.GetProperty("filesystemAvailableBytes").ValueKind);
        Assert.Equal(JsonValueKind.Null, volume.GetProperty("capacityScope").ValueKind);
    }

    private static Dictionary<string, string?> ProxyCatalog()
    {
        var catalog = Catalog();
        foreach (var (alias, kind) in new[] { ("demo-stub", "nginx"), ("demo-access", "nginx-access"), ("demo-cert", "tls"),
            ("demo-git", "gitea"), ("demo-ci", "gitea-actions"), ("demo-app", "docker-workload"), ("demo-data", "docker-volume") })
        {
            catalog.Add($"Dashboard:PublicServices:{alias}:Node", "public-edge");
            catalog.Add($"Dashboard:PublicServices:{alias}:Kind", kind);
        }

        catalog.Add("Dashboard:PublicServices:demo-app:Source", "private-workload");
        catalog.Add("Dashboard:PublicServices:demo-data:Source", "private-volume");
        return catalog;
    }

    private static object Stub(DateTimeOffset at, int requests) => new
    {
        status = "available",
        observedAt = at.UtcDateTime,
        requestsTotal = requests,
        activeConnections = 2,
        readingConnections = 0,
        writingConnections = 0,
        waitingConnections = 2,
        acceptedConnectionsTotal = 10,
        handledConnectionsTotal = 10,
    };

    private static Dictionary<string, object> Access(DateTimeOffset at, int requests, int errors, int? p95 = null, bool truncated = false)
    {
        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["status"] = "available",
            ["observedAt"] = at.UtcDateTime,
            ["windowSeconds"] = 300,
            ["requests"] = requests,
            ["serverErrors"] = errors,
            ["tailTruncated"] = truncated,
        };
        if (p95.HasValue)
        {
            payload.Add("requestTimeP95Ms", p95.Value);
        }

        return payload;
    }

    private static Dictionary<string, object> Actions(DateTimeOffset at, int timed, int? mean = null)
    {
        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["status"] = "available",
            ["observedAt"] = at.UtcDateTime,
            ["waitingJobs"] = 0,
            ["runningJobs"] = 2,
            ["blockedJobs"] = 0,
            ["successfulJobsLastHour"] = 2,
            ["failedJobsLastHour"] = 0,
            ["cancelledJobsLastHour"] = 0,
            ["skippedJobsLastHour"] = 0,
            ["activeRunners"] = 1,
            ["idleRunners"] = 0,
            ["offlineRunners"] = 0,
            ["timedJobsLastHour"] = timed,
        };
        if (mean.HasValue)
        {
            payload.Add("meanJobDurationSecondsLastHour", mean.Value);
        }

        return payload;
    }

    private static object Workload(DateTimeOffset at, int restartCount = 0) => new
    {
        status = "available",
        observedAt = at.UtcDateTime,
        state = "running",
        restartCount,
        restartPolicy = "no",
        usage = new { status = "unavailable" },
    };

    private static string? SignalKey(JsonElement root, string kind, string alias) => Service(root, kind, alias)
        .GetProperty("signals")[0].GetProperty("messageKey").GetString();

    private static Dictionary<string, object> HealthReadings(DateTimeOffset at, bool unhealthy) => new(StringComparer.Ordinal)
    {
        ["nginxAccess"] = Access(at, requests: 100, errors: unhealthy ? 10 : 0, p95: 100),
        ["tls"] = new { status = "available", observedAt = at.UtcDateTime,
            expiresAt = at.AddDays(unhealthy ? 5 : 30).UtcDateTime },
        ["gitea"] = new { status = "available", observedAt = at.UtcDateTime, databaseHealthy = true,
            cacheHealthy = true, responseTimeMs = unhealthy ? 1500 : 10 },
        ["giteaActions"] = new
        {
            status = "available", observedAt = at.UtcDateTime, waitingJobs = unhealthy ? 8 : 0, runningJobs = 0,
            blockedJobs = 0, successfulJobsLastHour = 0, failedJobsLastHour = 0, cancelledJobsLastHour = 0,
            skippedJobsLastHour = 0, activeRunners = 1, idleRunners = 1, offlineRunners = 0, timedJobsLastHour = 0,
        },
        ["dockerWorkloads"] = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["private-workload"] = new { status = "available", observedAt = at.UtcDateTime, state = "running",
                health = unhealthy ? "unhealthy" : "healthy", restartCount = 0, restartPolicy = "unless-stopped",
                usage = new { status = "unavailable" } },
        },
    };
}
