using System.Text.Json.Serialization;
using HomeLab.Pulse.Api.Dashboard.Signals;

namespace HomeLab.Pulse.Api.Dashboard.Services;

internal sealed record ServicesSnapshot(int SchemaVersion, DateTimeOffset GeneratedAt, IReadOnlyList<NodeServices> Nodes);

internal sealed record NodeServices
{
    public required string Alias { get; init; }

    [JsonPropertyName("postgresql")]
    public required IReadOnlyList<ServiceReading<PostgreSqlMetrics>> PostgreSql { get; init; }

    public required IReadOnlyList<ServiceReading<RedisMetrics>> Redis { get; init; }

    [JsonPropertyName("rabbitmq")]
    public required IReadOnlyList<ServiceReading<RabbitMqMetrics>> RabbitMq { get; init; }

    public required IReadOnlyList<ServiceReading<SqliteMetrics>> Sqlite { get; init; }

    public required IReadOnlyList<ServiceReading<NginxMetrics>> Nginx { get; init; }

    public required IReadOnlyList<ServiceReading<NginxAccessMetrics>> NginxAccess { get; init; }

    public required IReadOnlyList<ServiceReading<TlsMetrics>> Tls { get; init; }

    public required IReadOnlyList<ServiceReading<GiteaMetrics>> Gitea { get; init; }

    public required IReadOnlyList<ServiceReading<GiteaActionsMetrics>> GiteaActions { get; init; }

    public required IReadOnlyList<ServiceReading<DockerWorkloadMetrics>> DockerWorkloads { get; init; }

    public required IReadOnlyList<ServiceReading<DockerVolumeMetrics>> DockerVolumes { get; init; }
}

internal sealed record ServiceReading<T> where T : class
{
    public required string Alias { get; init; }

    public required string Status { get; init; }

    public DateTimeOffset? ObservedAt { get; init; }

    public T? Metrics { get; init; }

    public required IReadOnlyList<HealthSignal> Signals { get; init; }
}

internal sealed record PostgreSqlMetrics
{
    public bool? InRecovery { get; init; }
    public long? DatabaseBytes { get; init; }
    public long? ConnectedBackends { get; init; }
    public double? TransactionsPerSecond { get; init; }
    public double? BufferHitPercent { get; init; }
    public long? DeadlocksDelta { get; init; }
    public double? RateWindowSeconds { get; init; }
}

internal sealed record RedisMetrics
{
    public long? UsedMemoryBytes { get; init; }
    public long? MaxMemoryBytes { get; init; }
    public double? MemoryUtilizationPercent { get; init; }
    public long? OperationsPerSecond { get; init; }
    public long? ConnectedClients { get; init; }
    public long? AofCurrentSizeBytes { get; init; }
    public double? KeyspaceHitPercent { get; init; }
    public long? EvictedKeysDelta { get; init; }
    public double? RateWindowSeconds { get; init; }
}

internal sealed record RabbitMqMetrics
{
    public long? Messages { get; init; }
    public long? MessagesReady { get; init; }
    public long? MessagesUnacknowledged { get; init; }
    public long? Consumers { get; init; }
}

internal sealed record SqliteMetrics
{
    public long? DatabaseBytes { get; init; }
    public long? WalBytes { get; init; }
    public long? SharedMemoryBytes { get; init; }
    public long? RollbackJournalBytes { get; init; }
    public long? TotalBytes { get; init; }
}
