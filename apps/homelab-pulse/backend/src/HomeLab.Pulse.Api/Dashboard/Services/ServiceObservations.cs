using System.Text.Json;
using HomeLab.Pulse.Api.Telemetry.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HomeLab.Pulse.Api.Dashboard.Services;

internal sealed record ServiceObservation(DateTimeOffset ObservedAt, Guid BootId, bool Available, JsonElement Data);
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
                'sqliteStorage', json_extract(s."Payload", '$.values.sqliteStorage')
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
                (service.Kind is ServiceKind.PostgreSql or ServiceKind.Redis ? pair.Previous?.ObservedAt : pair.Latest.ObservedAt) >= envelopeAt))
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
                var timestamp = HostTelemetry.Property(item, "observedAt");
                var status = HostTelemetry.Property(item, "status");
                if (timestamp.ValueKind != JsonValueKind.String || !timestamp.TryGetDateTimeOffset(out var observedAt)
                    || observedAt > envelopeAt || status.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var pair = readings.GetValueOrDefault(service.Alias);
                if (pair is not null && (observedAt == pair.Latest.ObservedAt || observedAt <= pair.Previous?.ObservedAt))
                {
                    continue;
                }

                var current = new ServiceObservation(observedAt, bootId, status.GetString() == "available", item.Clone());
                readings[service.Alias] = pair is null ? new(current)
                    : observedAt > pair.Latest.ObservedAt ? new(current, pair.Latest) : pair with { Previous = current };
            }
        }

        return readings;
    }

    private static JsonElement Select(JsonElement values, ApprovedService service) => service.Kind switch
    {
        ServiceKind.PostgreSql => values.GetProperty("postgresql"),
        ServiceKind.Redis => values.GetProperty("redis"),
        ServiceKind.RabbitMq => values.GetProperty("rabbitmq"),
        ServiceKind.Sqlite => HostTelemetry.Property(values.GetProperty("sqliteStorage"), service.CollectorAlias!),
        _ => throw new InvalidOperationException("Unsupported dashboard service kind."),
    };

    private sealed record ServiceReport(long ObservedAtUtcTicks, string Readings);
}
