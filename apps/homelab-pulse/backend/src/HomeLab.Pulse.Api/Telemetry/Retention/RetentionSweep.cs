using HomeLab.Pulse.Api.Telemetry.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HomeLab.Pulse.Api.Telemetry.Retention;

internal sealed partial class RetentionSweep(
    TelemetryDbContext database,
    IOptions<RetentionOptions> options,
    TimeProvider clock,
    ILogger<RetentionSweep> logger)
{
    private const int MaxBatchesPerSweep = 128;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.GetUtcNow().AddDays(-options.Value.Days).UtcTicks;
        var batchSize = options.Value.BatchSize;
        var removed = 0;
        try
        {
            for (var batch = 0; batch < MaxBatchesPerSweep; batch++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Each write is an independent transaction and uses the observation-time index.
                var affected = await database.Database.ExecuteSqlInterpolatedAsync($"""
                    DELETE FROM "TelemetrySamples"
                    WHERE rowid IN (
                        SELECT rowid
                        FROM "TelemetrySamples"
                        WHERE "ObservedAtUtcTicks" < {cutoff}
                        ORDER BY "ObservedAtUtcTicks", rowid
                        LIMIT {batchSize}
                    )
                    """, cancellationToken);
                removed += affected;
                if (affected < batchSize)
                {
                    break;
                }
            }
        }
        catch (SqliteException exception)
        {
            LogSweepInterrupted(logger, exception.SqliteErrorCode, removed);
            return;
        }

        if (removed > 0)
        {
            LogSweepCompleted(logger, removed);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Telemetry retention removed {Removed} samples.")]
    private static partial void LogSweepCompleted(ILogger logger, int removed);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Telemetry retention stopped after removing {Removed} samples. SQLite error code {ErrorCode}.")]
    private static partial void LogSweepInterrupted(ILogger logger, int errorCode, int removed);
}
