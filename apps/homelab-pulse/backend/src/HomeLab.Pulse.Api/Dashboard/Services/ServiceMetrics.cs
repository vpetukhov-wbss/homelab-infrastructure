using System.Text.Json;

namespace HomeLab.Pulse.Api.Dashboard.Services;

internal static class ServiceMetrics
{
    internal static PostgreSqlMetrics PostgreSql(JsonElement latest, JsonElement previous, double? elapsed)
    {
        var inRecovery = Boolean(latest, "inRecovery");
        var reset = ResetTime(latest);
        long? commits = null, rollbacks = null, hits = null, reads = null, deadlocks = null;
        if (elapsed.HasValue && inRecovery == Boolean(previous, "inRecovery") && reset.HasValue && reset == ResetTime(previous))
        {
            commits = Change(latest, previous, "commitsTotal");
            rollbacks = Change(latest, previous, "rollbacksTotal");
            hits = Change(latest, previous, "bufferHitsTotal");
            reads = Change(latest, previous, "blocksReadTotal");
            deadlocks = Change(latest, previous, "deadlocksTotal");
        }

        var transactions = (double?)commits + rollbacks;
        var hitPercent = Percent(hits, reads);
        var continuous = commits >= 0 && rollbacks >= 0 && hits >= 0 && reads >= 0 && deadlocks >= 0;
        return new PostgreSqlMetrics(inRecovery, HostTelemetry.Bytes(latest, "databaseBytes"),
            HostTelemetry.Bytes(latest, "connectedBackends"), continuous ? transactions / elapsed : null,
            continuous ? hitPercent : null, continuous ? deadlocks : null, continuous ? elapsed : null);
    }

    internal static RedisMetrics Redis(JsonElement latest, JsonElement previous, double? elapsed)
    {
        var used = HostTelemetry.Bytes(latest, "usedMemoryBytes");
        var max = HostTelemetry.Bytes(latest, "maxMemoryBytes");
        var hits = Change(latest, previous, "keyspaceHitsTotal");
        var misses = Change(latest, previous, "keyspaceMissesTotal");
        var evictions = Change(latest, previous, "evictedKeysTotal");
        var continuous = elapsed.HasValue && hits >= 0 && misses >= 0 && evictions >= 0;
        var hitPercent = continuous ? Percent(hits, misses) : null;
        var evicted = continuous ? evictions : null;
        return new RedisMetrics(used, max, max > 0 ? used * 100.0 / max : null,
            HostTelemetry.Bytes(latest, "operationsPerSecond"), HostTelemetry.Bytes(latest, "connectedClients"),
            HostTelemetry.Bytes(latest, "aofCurrentSizeBytes"), hitPercent, evicted,
            hitPercent.HasValue || evicted.HasValue ? elapsed : null);
    }

    internal static RabbitMqMetrics RabbitMq(JsonElement latest) =>
        new(HostTelemetry.Bytes(latest, "messages"), HostTelemetry.Bytes(latest, "messagesReady"),
            HostTelemetry.Bytes(latest, "messagesUnacknowledged"), HostTelemetry.Bytes(latest, "consumers"));

    internal static SqliteMetrics Sqlite(JsonElement latest)
    {
        var database = HostTelemetry.Bytes(latest, "databaseBytes");
        var wal = HostTelemetry.Bytes(latest, "walBytes");
        var sharedMemory = HostTelemetry.Bytes(latest, "sharedMemoryBytes");
        var journal = HostTelemetry.Bytes(latest, "rollbackJournalBytes");
        var total = HostTelemetry.Bytes(latest, "totalBytes");
        var parts = (decimal?)database + wal + sharedMemory + journal;
        return new SqliteMetrics(database, wal, sharedMemory, journal, total == parts ? total : null);
    }

    internal static long? Change(JsonElement latest, JsonElement previous, string field) =>
        HostTelemetry.Bytes(latest, field) - HostTelemetry.Bytes(previous, field);

    private static double? Percent(long? numerator, long? remaining)
    {
        var total = (double?)numerator + remaining;
        return total > 0 ? numerator * 100.0 / total : null;
    }

    internal static bool? Boolean(JsonElement reading, string key)
    {
        var field = HostTelemetry.Property(reading, key);
        return field.ValueKind is JsonValueKind.True or JsonValueKind.False ? field.GetBoolean() : null;
    }

    private static DateTimeOffset? ResetTime(JsonElement reading)
    {
        var reset = HostTelemetry.Property(reading, "statsResetAt");
        var observed = HostTelemetry.Property(reading, "observedAt");
        return reset.ValueKind == JsonValueKind.String && reset.TryGetDateTimeOffset(out var resetAt)
            && observed.ValueKind == JsonValueKind.String && observed.TryGetDateTimeOffset(out var observedAt) && resetAt <= observedAt
            ? resetAt : null;
    }
}
