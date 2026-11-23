using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HomeLab.Pulse.Api.Dashboard.Services;

internal sealed partial class ServicesWorker(
    ServiceCatalog catalog, IServiceScopeFactory scopes, ServicesPublication publication, TimeProvider clock, ILogger<ServicesWorker> logger)
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
                var projection = await scope.ServiceProvider.GetRequiredService<ServicesProjection>().ReadAsync(now, stoppingToken);
                await publication.PublishAsync(projection, now, stoppingToken);
                if (catalog.Nodes.Count == 0)
                {
                    return;
                }
            }
            catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException or JsonException)
            {
                LogPublicationFailure(logger, error.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Services publication failed ({FailureType}); will retry.")]
    private static partial void LogPublicationFailure(ILogger logger, string failureType);
}
