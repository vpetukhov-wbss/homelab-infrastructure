using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HomeLab.Pulse.Api.Dashboard.Overview;

internal sealed partial class OverviewWorker(
    IServiceScopeFactory scopes, OverviewPublication publication, TimeProvider clock, ILogger<OverviewWorker> logger)
    : BackgroundService
{
    internal static readonly TimeSpan Frequency = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Frequency, clock);
        do
        {
            try
            {
                var now = clock.GetUtcNow();
                await using var scope = scopes.CreateAsyncScope();
                var nodes = await scope.ServiceProvider.GetRequiredService<OverviewProjection>().ReadAsync(now, stoppingToken);
                if (publication.Current?.Snapshot.Nodes.SequenceEqual(nodes) is true)
                {
                    continue;
                }

                var revisions = scope.ServiceProvider.GetRequiredService<DashboardRevisionStore>();
                var revision = await revisions.AllocateAsync(stoppingToken);
                await publication.PublishAsync(new OverviewSnapshot(1, revision, now, nodes), stoppingToken);
            }
            catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException or JsonException)
            {
                LogPublicationFailure(logger, error.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Overview publication failed ({FailureType}); will retry.")]
    private static partial void LogPublicationFailure(ILogger logger, string failureType);
}
