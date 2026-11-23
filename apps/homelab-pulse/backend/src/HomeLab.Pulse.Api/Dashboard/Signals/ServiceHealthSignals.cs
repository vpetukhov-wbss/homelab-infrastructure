using HomeLab.Pulse.Api.Dashboard.Services;
using Microsoft.Extensions.Options;

namespace HomeLab.Pulse.Api.Dashboard.Signals;

internal sealed class ServiceHealthSignals(IOptions<HealthSignalOptions> configured)
{
    private readonly HealthSignalOptions _options = configured.Value;

    public IReadOnlyList<HealthSignal> Evaluate<T>(ServiceSignalInput<T> input) where T : class
    {
        if (input.Status == "stale")
        {
            return [new HealthSignal
            {
                Severity = "warning",
                MessageKey = "service.ghosted_us",
                Window = "freshness",
            }];
        }

        if (input.Status == "unavailable")
        {
            return [new HealthSignal { Severity = "critical", MessageKey = "service.check_failed" }];
        }

        if (input.Status != "available" || input.Metrics is null)
        {
            return [];
        }

        return (input.Kind, input.Metrics) switch
        {
            (ServiceKind.PostgreSql, PostgreSqlMetrics value) when value.DeadlocksDelta > 0 =>
                [new HealthSignal { Severity = "warning", MessageKey = "postgres.traffic_jam", Value = value.DeadlocksDelta,
                    Unit = "deadlocks", Window = RateWindow(value.RateWindowSeconds), Threshold = 0 }],
            (ServiceKind.Redis, RedisMetrics value) when value.EvictedKeysDelta > 0 =>
                [new HealthSignal { Severity = "warning", MessageKey = "redis.kicking_keys_out", Value = value.EvictedKeysDelta,
                    Unit = "keys", Window = RateWindow(value.RateWindowSeconds), Threshold = 0 }],
            (ServiceKind.RabbitMq, RabbitMqMetrics value) when value.MessagesReady >= _options.RabbitReadyMessages =>
                [new HealthSignal { Severity = "warning", MessageKey = "rabbit.queue_growing", Value = value.MessagesReady,
                    Unit = "messages", Window = "current", Threshold = _options.RabbitReadyMessages }],
            (ServiceKind.NginxAccess, NginxAccessMetrics value) when value.TailTruncated == false
                && value.ServerErrorPercent >= _options.NginxServerErrorPercent =>
                [new HealthSignal { Severity = "warning", MessageKey = "nginx.proxy_busy", Value = value.ServerErrorPercent,
                    Unit = "%", Window = RateWindow(value.WindowSeconds), Threshold = _options.NginxServerErrorPercent }],
            (ServiceKind.Gitea, GiteaMetrics value) when value.DatabaseHealthy == false || value.CacheHealthy == false =>
                [new HealthSignal { Severity = "critical", MessageKey = "gitea.check_failed" }],
            (ServiceKind.Gitea, GiteaMetrics value) when value.ResponseTimeMs >= _options.GiteaResponseTimeMs =>
                [new HealthSignal { Severity = "warning", MessageKey = "gitea.feeling_slow", Value = value.ResponseTimeMs,
                    Unit = "ms", Window = "current", Threshold = _options.GiteaResponseTimeMs }],
            (ServiceKind.GiteaActions, GiteaActionsMetrics value) when value.WaitingJobs >= _options.CiWaitingJobs =>
                [new HealthSignal { Severity = "warning", MessageKey = "ci.queue_growing", Value = value.WaitingJobs,
                    Unit = "jobs", Window = "current", Threshold = _options.CiWaitingJobs }],
            (ServiceKind.DockerWorkload, DockerWorkloadMetrics value) when value.State is not null and not "running"
                || value.Health == "unhealthy" =>
                [new HealthSignal { Severity = "critical", MessageKey = "docker.workload_unhealthy" }],
            (ServiceKind.Tls, TlsMetrics value) when value.ExpiresAt is { } expiry
                && (expiry - input.EvaluatedAt).TotalDays <= _options.TlsExpiryDays =>
                [new HealthSignal { Severity = "warning", MessageKey = "tls.expiring_soon",
                    Value = Math.Max(0, (expiry - input.EvaluatedAt).TotalDays), Unit = "days", Window = "current",
                    Threshold = _options.TlsExpiryDays }],
            _ => [],
        };
    }

    private static string? RateWindow(double? seconds) => seconds.HasValue ? $"{seconds:0.#}s" : null;

    private static string? RateWindow(long? seconds) => seconds.HasValue ? $"{seconds:0.#}s" : null;
}

internal sealed record ServiceSignalInput<T> where T : class
{
    public required ServiceKind Kind { get; init; }

    public required string Status { get; init; }

    public DateTimeOffset? ObservedAt { get; init; }

    public T? Metrics { get; init; }

    public required DateTimeOffset EvaluatedAt { get; init; }
}
