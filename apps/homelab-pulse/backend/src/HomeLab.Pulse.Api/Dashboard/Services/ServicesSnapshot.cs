using System.Text.Json.Serialization;

namespace HomeLab.Pulse.Api.Dashboard.Services;

internal sealed record ServicesSnapshot(int SchemaVersion, DateTimeOffset GeneratedAt, IReadOnlyList<NodeServices> Nodes);

internal sealed record NodeServices(
    string Alias, [property: JsonPropertyName("postgresql")] IReadOnlyList<ServiceReading<PostgreSqlMetrics>> PostgreSql,
    IReadOnlyList<ServiceReading<RedisMetrics>> Redis,
    [property: JsonPropertyName("rabbitmq")] IReadOnlyList<ServiceReading<RabbitMqMetrics>> RabbitMq, IReadOnlyList<ServiceReading<SqliteMetrics>> Sqlite,
    IReadOnlyList<ServiceReading<NginxMetrics>> Nginx, IReadOnlyList<ServiceReading<NginxAccessMetrics>> NginxAccess,
    IReadOnlyList<ServiceReading<TlsMetrics>> Tls, IReadOnlyList<ServiceReading<GiteaMetrics>> Gitea,
    IReadOnlyList<ServiceReading<GiteaActionsMetrics>> GiteaActions,
    IReadOnlyList<ServiceReading<DockerWorkloadMetrics>> DockerWorkloads,
    IReadOnlyList<ServiceReading<DockerVolumeMetrics>> DockerVolumes);

internal sealed record ServiceReading<T>(string Alias, string Status, DateTimeOffset? ObservedAt, T? Metrics) where T : class;

internal sealed record PostgreSqlMetrics(
    bool? InRecovery, long? DatabaseBytes, long? ConnectedBackends, double? TransactionsPerSecond,
    double? BufferHitPercent, long? DeadlocksDelta, double? RateWindowSeconds);

internal sealed record RedisMetrics(
    long? UsedMemoryBytes, long? MaxMemoryBytes, double? MemoryUtilizationPercent, long? OperationsPerSecond,
    long? ConnectedClients, long? AofCurrentSizeBytes, double? KeyspaceHitPercent, long? EvictedKeysDelta, double? RateWindowSeconds);

internal sealed record RabbitMqMetrics(long? Messages, long? MessagesReady, long? MessagesUnacknowledged, long? Consumers);

internal sealed record SqliteMetrics(long? DatabaseBytes, long? WalBytes, long? SharedMemoryBytes, long? RollbackJournalBytes, long? TotalBytes);
