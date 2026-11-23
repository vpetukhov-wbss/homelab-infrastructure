using HomeLab.Pulse.Api.Telemetry.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HomeLab.Pulse.Api.Dashboard.Overview;

internal sealed class DashboardRevisionStore(TelemetryDbContext database)
{
    public async Task<long> AllocateAsync(CancellationToken cancellationToken)
    {
        await database.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = database.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                UPDATE "DashboardRevision"
                SET "Value" = "Value" + 1
                WHERE "Id" = 1 AND "Value" < 9223372036854775807
                RETURNING "Value"
                """;
            return await command.ExecuteScalarAsync(cancellationToken) is long revision
                ? revision : throw new IOException("Dashboard revision could not be allocated.");
        }
        finally
        {
            await database.Database.CloseConnectionAsync();
        }
    }
}
