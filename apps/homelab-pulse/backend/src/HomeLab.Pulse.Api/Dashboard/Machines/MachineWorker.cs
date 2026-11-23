using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace HomeLab.Pulse.Api.Dashboard.Machines;

internal sealed partial class MachineWorker(
    MachineCatalog catalog, IServiceScopeFactory scopes, MachinePublication publication, TimeProvider clock, ILogger<MachineWorker> logger)
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
                var machines = await scope.ServiceProvider.GetRequiredService<MachineProjection>().ReadAsync(now, stoppingToken);
                await publication.PublishAsync(machines, now, stoppingToken);
            }
            catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException or JsonException)
            {
                LogPublicationFailure(logger, error.GetType().Name);
            }
        }
        while (catalog.Machines.Count > 0 && await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Machine details publication failed ({FailureType}); will retry.")]
    private static partial void LogPublicationFailure(ILogger logger, string failureType);
}
