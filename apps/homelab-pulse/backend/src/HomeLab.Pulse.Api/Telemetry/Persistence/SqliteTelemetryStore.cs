using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HomeLab.Pulse.Api.Telemetry.Persistence;

internal sealed partial class SqliteTelemetryStore(
    TelemetryDbContext database, ILogger<SqliteTelemetryStore> logger) : ITelemetryStore
{
    public async Task<bool> TrySaveAsync(TelemetrySample sample, CancellationToken cancellationToken)
    {
        try
        {
            // SQLite commits this single statement atomically. Only a conflict on the sample key is ignored.
            await database.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "TelemetrySamples" ("AgentId", "SampleId", "ObservedAtUtcTicks", "ReceivedAtUtcTicks", "Payload")
                VALUES ({sample.AgentId}, {sample.SampleId}, {sample.ObservedAtUtcTicks}, {sample.ReceivedAtUtcTicks}, {sample.Payload})
                ON CONFLICT ("AgentId", "SampleId") DO NOTHING
                """, cancellationToken);
            return true;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6 or 8 or 10 or 13 or 14)
        {
            LogStorageUnavailable(logger, exception.SqliteErrorCode);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "SQLite could not accept a sample. Error code {ErrorCode}.")]
    private static partial void LogStorageUnavailable(ILogger logger, int errorCode);
}
