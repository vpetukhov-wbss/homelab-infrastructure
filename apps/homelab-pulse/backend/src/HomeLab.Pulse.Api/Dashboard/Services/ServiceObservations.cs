using System.Text.Json;
using HomeLab.Pulse.Api.Telemetry.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HomeLab.Pulse.Api.Dashboard.Services;

internal sealed record ServiceObservation(
    DateTimeOffset EffectiveAt, DateTimeOffset? ObservedAt, Guid BootId, string Status, JsonElement Data);
internal sealed record ServiceObservationPair(ServiceObservation Latest, ServiceObservation? Previous = null);

internal sealed class ServiceObservations(TelemetryDbContext database)
{
    private const int MaxRecentSamples = 64;

    public async Task<Dictionary<string, ServiceObservationPair>> ReadAsync(
        ServiceNode node, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var history = database.Database.SqlQuery<ServiceReport>($"""
            SELECT s."ObservedAtUtcTicks", json_object(
                'bootId', json_extract(s."Payload", '$.values.bootId'),
                'postgresql', json_extract(s."Payload", '$.values.postgresql'),
                'redis', json_extract(s."Payload", '$.values.redis'),
                'rabbitmq', json_extract(s."Payload", '$.values.rabbitmq'),
                'sqliteStorage', json_extract(s."Payload", '$.values.sqliteStorage'),
                'nginx', json_extract(s."Payload", '$.values.nginx'),
                'nginxAccess', json_extract(s."Payload", '$.values.nginxAccess'),
                'tls', json_extract(s."Payload", '$.values.tls'),
                'gitea', json_extract(s."Payload", '$.values.gitea'),
                'giteaActions', json_extract(s."Payload", '$.values.giteaActions'),
                'dockerWorkloads', json_extract(s."Payload", '$.values.dockerWorkloads'),
                'dockerVolumes', json_extract(s."Payload", '$.values.dockerVolumes')
            ) AS "Readings"
            FROM (
                SELECT "ObservedAtUtcTicks", "ReceivedAtUtcTicks", "SampleId", "Payload"
                FROM "TelemetrySamples"
                WHERE "AgentId" = {node.AgentId} AND "ObservedAtUtcTicks" <= {now.UtcTicks}
                ORDER BY "ObservedAtUtcTicks" DESC, "ReceivedAtUtcTicks" DESC, "SampleId" DESC
                LIMIT {MaxRecentSamples}
            ) AS s
            ORDER BY s."ObservedAtUtcTicks" DESC, s."ReceivedAtUtcTicks" DESC, s."SampleId" DESC
            """);
        var readings = new Dictionary<string, ServiceObservationPair>(StringComparer.Ordinal);
        await foreach (var report in history.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            var envelopeAt = new DateTimeOffset(report.ObservedAtUtcTicks, TimeSpan.Zero);
            if (node.Services.All(service => readings.TryGetValue(service.Alias, out var pair) &&
                (service.Kind is ServiceKind.PostgreSql or ServiceKind.Redis or ServiceKind.Nginx
                    ? pair.Previous?.EffectiveAt : pair.Latest.EffectiveAt) >= envelopeAt))
            {
                break;
            }

            using var json = JsonDocument.Parse(report.Readings);
            var boot = json.RootElement.GetProperty("bootId");
            if (boot.ValueKind != JsonValueKind.String || !boot.TryGetGuid(out var bootId))
            {
                continue;
            }

            foreach (var service in node.Services)
            {
                var item = Select(json.RootElement, service);
                if (Read(item, service.Kind, envelopeAt, bootId) is not { } current)
                {
                    continue;
                }

                var pair = readings.GetValueOrDefault(service.Alias);
                if (pair is not null && (current.EffectiveAt == pair.Latest.EffectiveAt || current.EffectiveAt <= pair.Previous?.EffectiveAt))
                {
                    continue;
                }

                readings[service.Alias] = pair is null ? new(current)
                    : current.EffectiveAt > pair.Latest.EffectiveAt ? new(current, pair.Latest) : pair with { Previous = current };
            }
        }

        return readings;
    }

    private static ServiceObservation? Read(JsonElement reading, ServiceKind kind, DateTimeOffset envelopeAt, Guid bootId)
    {
        var status = HostTelemetry.Property(reading, "status");
        var state = status.ValueKind == JsonValueKind.String ? status.GetString() : null;
        if (state is not ("available" or "unavailable" or "stale"))
        {
            return null;
        }

        var timestamp = HostTelemetry.Property(reading, "observedAt");
        if (timestamp.ValueKind == JsonValueKind.String && timestamp.TryGetDateTimeOffset(out var observedAt) && observedAt <= envelopeAt)
        {
            return new ServiceObservation(observedAt, observedAt, bootId, state, reading.Clone());
        }

        return state == "unavailable" && timestamp.ValueKind == JsonValueKind.Undefined
            && kind is ServiceKind.GiteaActions or ServiceKind.DockerWorkload or ServiceKind.DockerVolume
            ? new ServiceObservation(envelopeAt, null, bootId, state, default) : null;
    }

    private static JsonElement Select(JsonElement values, ApprovedService service) => service.Kind switch
    {
        ServiceKind.PostgreSql => values.GetProperty("postgresql"),
        ServiceKind.Redis => values.GetProperty("redis"),
        ServiceKind.RabbitMq => values.GetProperty("rabbitmq"),
        ServiceKind.Sqlite => HostTelemetry.Property(values.GetProperty("sqliteStorage"), service.CollectorAlias!),
        ServiceKind.Nginx => values.GetProperty("nginx"),
        ServiceKind.NginxAccess => values.GetProperty("nginxAccess"),
        ServiceKind.Tls => values.GetProperty("tls"),
        ServiceKind.Gitea => values.GetProperty("gitea"),
        ServiceKind.GiteaActions => values.GetProperty("giteaActions"),
        ServiceKind.DockerWorkload => HostTelemetry.Property(values.GetProperty("dockerWorkloads"), service.CollectorAlias!),
        ServiceKind.DockerVolume => HostTelemetry.Property(values.GetProperty("dockerVolumes"), service.CollectorAlias!),
        _ => throw new InvalidOperationException("Unsupported dashboard service kind."),
    };

    private sealed record ServiceReport(long ObservedAtUtcTicks, string Readings);
}
