using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using HomeLab.Pulse.Api.Telemetry.Retention;

namespace HomeLab.Pulse.Api.IntegrationTests;

internal sealed class PulseApiFactory(
    string path, string environment = "Production", TimeProvider? clock = null,
    int? retentionBatchSize = null, bool runRetentionWorker = true) : WebApplicationFactory<Program>
{
    public static string AgentToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static string OtherAgentToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static SqliteConnection Connect(string path) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false,
    }.ToString());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Telemetry:DatabasePath"] = path,
                ["Telemetry:Retention:BatchSize"] = retentionBatchSize?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "512",
                ["Telemetry:Agents:demo-a"] = Hash(AgentToken),
                ["Telemetry:Agents:demo-b"] = Hash(OtherAgentToken),
            }));
        builder.ConfigureServices(services =>
        {
            if (clock is not null)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(clock);
            }

            if (!runRetentionWorker)
            {
                services.Remove(services.Single(item => item.ImplementationType == typeof(RetentionWorker)));
            }
        });
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
}
