using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace HomeLab.Pulse.Api.Telemetry.Persistence;

internal sealed record TelemetryStorageSettings(string DatabasePath);

internal static class TelemetryDatabase
{
    public static string ConnectionString(string databasePath) => new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        DefaultTimeout = 1,
    }.ToString();

    public static async Task InitializeAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var path = services.GetRequiredService<TelemetryStorageSettings>().DatabasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("A directory is required for the telemetry database."));

        await using var scope = services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();
        await database.Database.MigrateAsync(cancellationToken);
        await database.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var pragma = database.Database.GetDbConnection().CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL";
            if (!string.Equals(await pragma.ExecuteScalarAsync(cancellationToken) as string, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The telemetry database requires WAL mode on a local disk.");
            }
        }
        finally
        {
            await database.Database.CloseConnectionAsync();
        }
    }
}
