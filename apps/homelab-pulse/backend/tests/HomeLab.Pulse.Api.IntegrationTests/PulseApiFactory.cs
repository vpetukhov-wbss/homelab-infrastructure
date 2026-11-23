using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace HomeLab.Pulse.Api.IntegrationTests;

internal sealed class PulseApiFactory(string path, string environment = "Production") : WebApplicationFactory<Program>
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
                ["Telemetry:Agents:demo-a"] = Hash(AgentToken),
                ["Telemetry:Agents:demo-b"] = Hash(OtherAgentToken),
            }));
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token)));
}
