using System.Text.Json;
using HomeLab.Pulse.Api.Telemetry.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HomeLab.Pulse.Api.Dashboard.Machines;

internal sealed class SmartProjection(TelemetryDbContext database)
{
    internal static readonly TimeSpan Freshness = TimeSpan.FromMinutes(3);
    private const int MaxRecentSamples = 64;

    public async Task<Dictionary<string, SmartReadout>> ReadAsync(
        ApprovedMachine machine, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (machine.Disks.Count == 0)
        {
            return new(StringComparer.Ordinal);
        }

        var reports = database.Database.SqlQuery<SmartReport>($"""
            SELECT s."ObservedAtUtcTicks", json_extract(s."Payload", '$.values.diskSmart') AS "Readings"
            FROM (
                SELECT "ObservedAtUtcTicks", "ReceivedAtUtcTicks", "SampleId", "Payload"
                FROM "TelemetrySamples"
                WHERE "AgentId" = {machine.Node.AgentId} AND "ObservedAtUtcTicks" <= {now.UtcTicks}
                ORDER BY "ObservedAtUtcTicks" DESC, "ReceivedAtUtcTicks" DESC, "SampleId" DESC
                LIMIT {MaxRecentSamples}
            ) AS s
            WHERE json_type(s."Payload", '$.values.diskSmart') = 'object'
            ORDER BY s."ObservedAtUtcTicks" DESC, s."ReceivedAtUtcTicks" DESC, s."SampleId" DESC
            """);

        var latest = new Dictionary<string, (DateTimeOffset ReportedAt, SmartReadout Reading)>(StringComparer.Ordinal);
        await foreach (var report in reports.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            var envelope = new DateTimeOffset(report.ObservedAtUtcTicks, TimeSpan.Zero);
            if (latest.Count == machine.Disks.Count && latest.Values.All(item => item.ReportedAt >= envelope))
            {
                break;
            }

            using var json = JsonDocument.Parse(report.Readings);
            foreach (var disk in machine.Disks)
            {
                if (json.RootElement.TryGetProperty(disk.Source, out var reading)
                    && Project(reading, envelope, now) is { } candidate
                    && (!latest.TryGetValue(disk.Source, out var known) || candidate.ReportedAt > known.ReportedAt))
                {
                    latest[disk.Source] = candidate;
                }
            }
        }

        return latest.ToDictionary(item => item.Key, item => item.Value.Reading, StringComparer.Ordinal);
    }

    private static (DateTimeOffset ReportedAt, SmartReadout Reading)? Project(
        JsonElement reading, DateTimeOffset envelope, DateTimeOffset now)
    {
        var status = HostTelemetry.Property(reading, "status");
        if (status.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var state = status.GetString();
        if (state == "unavailable")
        {
            return (envelope, new SmartReadout { Status = "unavailable" });
        }

        var instant = HostTelemetry.Property(reading, "observedAt");
        if (instant.ValueKind != JsonValueKind.String || !instant.TryGetDateTimeOffset(out var observedAt) || observedAt > envelope)
        {
            return null;
        }

        if (state == "stale" || now - observedAt >= Freshness)
        {
            return (observedAt, new SmartReadout { Status = "stale", ObservedAt = observedAt });
        }

        var temperature = HostTelemetry.Property(reading, "temperatureCelsius");
        var health = HostTelemetry.Property(reading, "healthPassed");
        return (observedAt, new SmartReadout
        {
            Status = "available",
            ObservedAt = observedAt,
            TemperatureCelsius = temperature.ValueKind == JsonValueKind.Number && temperature.TryGetInt64(out var celsius) ? celsius : null,
            HealthPassed = health.ValueKind is JsonValueKind.True or JsonValueKind.False ? health.GetBoolean() : null,
            NvmePercentageUsed = HostTelemetry.Bytes(reading, "nvmePercentageUsed"),
            NvmeMediaErrors = HostTelemetry.Bytes(reading, "nvmeMediaErrors"),
            ReallocatedSectors = HostTelemetry.Bytes(reading, "reallocatedSectors"),
            PendingSectors = HostTelemetry.Bytes(reading, "pendingSectors"),
            OfflineUncorrectableSectors = HostTelemetry.Bytes(reading, "offlineUncorrectableSectors"),
        });
    }

    private sealed record SmartReport(long ObservedAtUtcTicks, string Readings);
}
