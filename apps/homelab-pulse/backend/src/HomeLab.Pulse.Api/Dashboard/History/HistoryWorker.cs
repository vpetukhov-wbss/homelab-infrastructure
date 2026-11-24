using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HomeLab.Pulse.Api.Dashboard.History;

internal sealed partial class HistoryWorker(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    ILogger<HistoryWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan Frequency = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan MaximumSnapshotAge = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Frequency, clock);
        do
        {
            try
            {
                var now = clock.GetUtcNow();
                await using var scope = scopes.CreateAsyncScope();
                var projection = await scope.ServiceProvider.GetRequiredService<HistoryProjection>().ReadAsync(now, stoppingToken);
                await scope.ServiceProvider.GetRequiredService<HistoryPublication>().PublishAsync(projection, now, stoppingToken);
            }
            catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException or JsonException)
            {
                LogPublicationFailure(logger, error.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "History publication failed ({FailureType}); will retry.")]
    private static partial void LogPublicationFailure(ILogger logger, string failureType);
}
