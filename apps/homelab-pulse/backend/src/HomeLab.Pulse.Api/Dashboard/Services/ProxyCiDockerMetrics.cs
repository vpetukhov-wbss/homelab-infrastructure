using System.Text.Json;

namespace HomeLab.Pulse.Api.Dashboard.Services;

internal sealed record NginxMetrics(
    long? ActiveConnections, long? ReadingConnections, long? WritingConnections, long? WaitingConnections,
    long? AcceptedConnectionsTotal, long? HandledConnectionsTotal, long? RequestsTotal,
    double? RequestsPerSecond, double? RateWindowSeconds);

internal sealed record NginxAccessMetrics(
    long? WindowSeconds, long? Requests, long? ServerErrors, bool? TailTruncated, long? RequestTimeP95Ms, double? ServerErrorPercent);

internal sealed record TlsMetrics(DateTimeOffset? ExpiresAt);

internal sealed record GiteaMetrics(bool? DatabaseHealthy, bool? CacheHealthy, long? ResponseTimeMs);

internal sealed record GiteaActionsMetrics(
    long? WaitingJobs, long? RunningJobs, long? BlockedJobs, long? SuccessfulJobsLastHour, long? FailedJobsLastHour,
    long? CancelledJobsLastHour, long? SkippedJobsLastHour, long? ActiveRunners, long? IdleRunners, long? OfflineRunners,
    long? TimedJobsLastHour, long? MeanJobDurationSecondsLastHour);

internal sealed record DockerWorkloadMetrics(
    string? State, string? Health, long? RestartCount, string? RestartPolicy, long? MemoryLimitBytes, double? CpuLimitCores,
    DockerUsage? Usage, IReadOnlyList<DockerVolumeAttachment>? VolumeAttachments);

internal sealed record DockerUsage(string Status, double? CpuUsageCores, long? MemoryUsageBytes);

internal sealed record DockerVolumeAttachment(string VolumeAlias, string Mode);

internal sealed record DockerVolumeMetrics(
    long? UsedBytes, string? CapacityScope, long? FilesystemCapacityBytes, long? FilesystemAvailableBytes, long? QuotaBytes);

internal static class ProxyCiDockerMetrics
{
    internal static NginxMetrics Nginx(JsonElement latest, JsonElement previous, double? elapsed)
    {
        var requests = ServiceMetrics.Change(latest, previous, "requestsTotal");
        var accepted = ServiceMetrics.Change(latest, previous, "acceptedConnectionsTotal");
        var handled = ServiceMetrics.Change(latest, previous, "handledConnectionsTotal");
        var valid = elapsed.HasValue && requests >= 0 && accepted >= 0 && handled >= 0;
        return new NginxMetrics(Count(latest, "activeConnections"), Count(latest, "readingConnections"),
            Count(latest, "writingConnections"), Count(latest, "waitingConnections"), Count(latest, "acceptedConnectionsTotal"),
            Count(latest, "handledConnectionsTotal"), Count(latest, "requestsTotal"), valid ? requests / elapsed : null, valid ? elapsed : null);
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
        return new NginxAccessMetrics(Count(latest, "windowSeconds"), requests, errors, truncated,
            complete ? Count(latest, "requestTimeP95Ms") : null, complete ? errors * 100.0 / requests : null);
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
        return new GiteaActionsMetrics(Count(latest, "waitingJobs"), Count(latest, "runningJobs"), Count(latest, "blockedJobs"),
            Count(latest, "successfulJobsLastHour"), Count(latest, "failedJobsLastHour"), Count(latest, "cancelledJobsLastHour"),
            Count(latest, "skippedJobsLastHour"), Count(latest, "activeRunners"), Count(latest, "idleRunners"), Count(latest, "offlineRunners"),
            timed, timed > 0 ? Count(latest, "meanJobDurationSecondsLastHour") : null);
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
        return new DockerWorkloadMetrics(
            state is "created" or "running" or "paused" or "restarting" or "removing" or "exited" or "dead" ? state : null,
            health is "starting" or "healthy" or "unhealthy" ? health : null,
            Count(latest, "restartCount"), policy is "no" or "always" or "on-failure" or "unless-stopped" ? policy : null,
            memoryLimit > 0 ? memoryLimit : null, limit > 0 ? limit : null, Usage(latest), approved);
    }

    internal static DockerVolumeMetrics DockerVolume(JsonElement latest)
    {
        var capacity = Count(latest, "filesystemCapacityBytes");
        var free = Count(latest, "filesystemAvailableBytes");
        var shared = capacity.HasValue && free <= capacity;
        return new DockerVolumeMetrics(Count(latest, "usedBytes"), shared ? "sharedFilesystem" : null,
            shared ? capacity : null, shared ? free : null, QuotaBytes: null);
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
