using HomeLab.Pulse.Api.Telemetry.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HomeLab.Pulse.Api.Hosting;

internal sealed class TelemetryReadiness(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<TelemetryDbContext>();
        try
        {
            _ = await database.Samples.AnyAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (SqliteException)
        {
            return HealthCheckResult.Unhealthy("Telemetry storage is unavailable.");
        }
    }
}
