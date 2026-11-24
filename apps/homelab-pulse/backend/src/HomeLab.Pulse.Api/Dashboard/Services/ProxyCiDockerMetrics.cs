using System.Text.Json;

namespace HomeLab.Pulse.Api.Dashboard.Services;

internal sealed record NginxMetrics
{
    public long? ActiveConnections { get; init; }
    public long? ReadingConnections { get; init; }
    public long? WritingConnections { get; init; }
    public long? WaitingConnections { get; init; }
    public long? AcceptedConnectionsTotal { get; init; }
    public long? HandledConnectionsTotal { get; init; }
    public long? RequestsTotal { get; init; }
    public double? RequestsPerSecond { get; init; }
    public double? RateWindowSeconds { get; init; }
}

internal sealed record NginxAccessMetrics
{
    public long? WindowSeconds { get; init; }
    public long? Requests { get; init; }
    public long? ServerErrors { get; init; }
    public bool? TailTruncated { get; init; }
    public long? RequestTimeP95Ms { get; init; }
    public double? ServerErrorPercent { get; init; }
}

internal sealed record TlsMetrics(DateTimeOffset? ExpiresAt);

internal sealed record GiteaMetrics(bool? DatabaseHealthy, bool? CacheHealthy, long? ResponseTimeMs);

internal sealed record GiteaActionsMetrics
{
    public long? WaitingJobs { get; init; }
    public long? RunningJobs { get; init; }
    public long? BlockedJobs { get; init; }
    public long? SuccessfulJobsLastHour { get; init; }
    public long? FailedJobsLastHour { get; init; }
    public long? CancelledJobsLastHour { get; init; }
    public long? SkippedJobsLastHour { get; init; }
    public long? ActiveRunners { get; init; }
    public long? IdleRunners { get; init; }
    public long? OfflineRunners { get; init; }
    public long? TimedJobsLastHour { get; init; }
    public long? MeanJobDurationSecondsLastHour { get; init; }
}

internal sealed record DockerWorkloadMetrics
{
    public string? State { get; init; }
    public string? Health { get; init; }
    public long? RestartCount { get; init; }
    public string? RestartPolicy { get; init; }
    public long? MemoryLimitBytes { get; init; }
    public double? CpuLimitCores { get; init; }
    public DockerUsage? Usage { get; init; }
    public IReadOnlyList<DockerVolumeAttachment>? VolumeAttachments { get; init; }
}

internal sealed record DockerUsage(string Status, double? CpuUsageCores, long? MemoryUsageBytes);

internal sealed record DockerVolumeAttachment(string VolumeAlias, string Mode);

internal sealed record DockerVolumeMetrics
{
    public long? UsedBytes { get; init; }
    public string? CapacityScope { get; init; }
    public long? FilesystemCapacityBytes { get; init; }
    public long? FilesystemAvailableBytes { get; init; }
    public long? QuotaBytes { get; init; }
}

internal static class ProxyCiDockerMetrics
{
    internal static NginxMetrics Nginx(JsonElement latest, JsonElement previous, double? elapsed)
    {
        var requests = ServiceMetrics.Change(latest, previous, "requestsTotal");
        var accepted = ServiceMetrics.Change(latest, previous, "acceptedConnectionsTotal");
        var handled = ServiceMetrics.Change(latest, previous, "handledConnectionsTotal");
        var valid = elapsed.HasValue && requests >= 0 && accepted >= 0 && handled >= 0;
        return new NginxMetrics
        {
            ActiveConnections = Count(latest, "activeConnections"),
            ReadingConnections = Count(latest, "readingConnections"),
            WritingConnections = Count(latest, "writingConnections"),
            WaitingConnections = Count(latest, "waitingConnections"),
            AcceptedConnectionsTotal = Count(latest, "acceptedConnectionsTotal"),
            HandledConnectionsTotal = Count(latest, "handledConnectionsTotal"),
            RequestsTotal = Count(latest, "requestsTotal"),
            RequestsPerSecond = valid ? requests / elapsed : null,
            RateWindowSeconds = valid ? elapsed : null,
        };
    }

    internal static NginxAccessMetrics NginxAccess(JsonElement latest)
    {
        var truncated = ServiceMetrics.Boolean(latest, "tailTruncated");
        var requests = Count(latest, "requests");
        var errors = Count(latest, "serverErrors");
        if (errors > requests)
        {
            errors = null;
        }

        var complete = truncated == false && requests > 0;
        return new NginxAccessMetrics
        {
            WindowSeconds = Count(latest, "windowSeconds"),
            Requests = requests,
            ServerErrors = errors,
            TailTruncated = truncated,
            RequestTimeP95Ms = complete ? Count(latest, "requestTimeP95Ms") : null,
            ServerErrorPercent = complete ? errors * 100.0 / requests : null,
        };
    }

    internal static TlsMetrics Tls(JsonElement latest)
    {
        var expiry = HostTelemetry.Property(latest, "expiresAt");
        return new TlsMetrics(expiry.ValueKind == JsonValueKind.String && expiry.TryGetDateTimeOffset(out var at) ? at : null);
    }

    internal static GiteaMetrics Gitea(JsonElement latest) =>
        new(ServiceMetrics.Boolean(latest, "databaseHealthy"), ServiceMetrics.Boolean(latest, "cacheHealthy"), Count(latest, "responseTimeMs"));

    internal static GiteaActionsMetrics GiteaActions(JsonElement latest)
    {
        var timed = Count(latest, "timedJobsLastHour");
        return new GiteaActionsMetrics
        {
            WaitingJobs = Count(latest, "waitingJobs"),
            RunningJobs = Count(latest, "runningJobs"),
            BlockedJobs = Count(latest, "blockedJobs"),
            SuccessfulJobsLastHour = Count(latest, "successfulJobsLastHour"),
            FailedJobsLastHour = Count(latest, "failedJobsLastHour"),
            CancelledJobsLastHour = Count(latest, "cancelledJobsLastHour"),
            SkippedJobsLastHour = Count(latest, "skippedJobsLastHour"),
            ActiveRunners = Count(latest, "activeRunners"),
            IdleRunners = Count(latest, "idleRunners"),
            OfflineRunners = Count(latest, "offlineRunners"),
            TimedJobsLastHour = timed,
            MeanJobDurationSecondsLastHour = timed > 0 ? Count(latest, "meanJobDurationSecondsLastHour") : null,
        };
    }

    internal static DockerWorkloadMetrics DockerWorkload(JsonElement latest, IReadOnlyList<ApprovedService> volumes)
    {
        var attachments = HostTelemetry.Property(latest, "volumeAttachments");
        List<DockerVolumeAttachment>? approved = null;
        if (attachments.ValueKind == JsonValueKind.Object)
        {
            approved = [];
            foreach (var volume in volumes)
            {
                var mode = Label(attachments, volume.CollectorAlias!);
                if (mode is "ro" or "rw" or "mixed")
                {
                    approved.Add(new DockerVolumeAttachment(volume.Alias, mode));
                }
            }
        }

        var state = Label(latest, "state");
        var health = Label(latest, "health");
        var policy = Label(latest, "restartPolicy");
        var limit = HostTelemetry.Number(latest, "cpuLimitCores");
        var memoryLimit = Count(latest, "memoryLimitBytes");
        return new DockerWorkloadMetrics
        {
            State = state is "created" or "running" or "paused" or "restarting" or "removing" or "exited" or "dead" ? state : null,
            Health = health is "starting" or "healthy" or "unhealthy" ? health : null,
            RestartCount = Count(latest, "restartCount"),
            RestartPolicy = policy is "no" or "always" or "on-failure" or "unless-stopped" ? policy : null,
            MemoryLimitBytes = memoryLimit > 0 ? memoryLimit : null,
            CpuLimitCores = limit > 0 ? limit : null,
            Usage = Usage(latest),
            VolumeAttachments = approved,
        };
    }

    internal static DockerVolumeMetrics DockerVolume(JsonElement latest)
    {
        var capacity = Count(latest, "filesystemCapacityBytes");
        var free = Count(latest, "filesystemAvailableBytes");
        var shared = capacity.HasValue && free <= capacity;
        return new DockerVolumeMetrics
        {
            UsedBytes = Count(latest, "usedBytes"),
            CapacityScope = shared ? "sharedFilesystem" : null,
            FilesystemCapacityBytes = shared ? capacity : null,
            FilesystemAvailableBytes = shared ? free : null,
            QuotaBytes = null,
        };
    }

    private static DockerUsage? Usage(JsonElement latest)
    {
        var usage = HostTelemetry.Property(latest, "usage");
        var status = Label(usage, "status");
        if (status == "unavailable")
        {
            return new DockerUsage("unavailable", null, null);
        }

        if (status != "available")
        {
            return null;
        }

        var cpu = HostTelemetry.Number(usage, "cpuUsageCores");
        return new DockerUsage("available", cpu >= 0 ? cpu : null, Count(usage, "memoryUsageBytes"));
    }

    private static string? Label(JsonElement source, string name)
    {
        var value = HostTelemetry.Property(source, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static long? Count(JsonElement source, string name) => HostTelemetry.Bytes(source, name);
}
