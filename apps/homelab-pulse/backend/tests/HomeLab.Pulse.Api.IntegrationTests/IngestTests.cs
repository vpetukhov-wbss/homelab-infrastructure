using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Xunit;

namespace HomeLab.Pulse.Api.IntegrationTests;

public sealed class IngestTests : IDisposable
{
    private readonly DirectoryInfo _temporary = Directory.CreateTempSubdirectory("pulse-api-tests-");

    private const string IngestPath = "/internal/v1/telemetry";

    private string DatabasePath => Path.Combine(_temporary.FullName, "pulse.db");

    [Fact]
    public async Task ConcurrentRetriesCommitExactlyOneRecordPerAuthenticatedAgent()
    {
        await using var app = new PulseApiFactory(DatabasePath);
        app.UseKestrel(0);
        using var client = app.CreateClient();
        var payload = Sample();
        var responses = await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(_ => PostAsync(client, payload, PulseApiFactory.AgentToken)));

        foreach (var response in responses)
        {
            using (response)
            {
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            }
        }

        Assert.Equal(1L, await CountAsync());
        using var other = await PostAsync(client, payload, PulseApiFactory.OtherAgentToken);
        Assert.Equal(HttpStatusCode.NoContent, other.StatusCode);
        Assert.Equal(2L, await CountAsync());
    }

    [Fact]
    public async Task ACommittedSampleRemainsIdempotentAfterTheHostRestarts()
    {
        var payload = Sample();
        await using (var first = new PulseApiFactory(DatabasePath))
        {
            using var client = first.CreateClient();
            using var response = await PostAsync(client, payload, PulseApiFactory.AgentToken);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        await using var second = new PulseApiFactory(DatabasePath);
        using var restarted = second.CreateClient();
        using var replay = await PostAsync(restarted, payload, PulseApiFactory.AgentToken);
        Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode);
        Assert.Equal(1L, await CountAsync());
    }

    [Fact]
    public async Task InvalidCredentialsDoNotReachStorage()
    {
        await using var app = new PulseApiFactory(DatabasePath);
        using var client = app.CreateClient();
        using var missing = await PostAsync(client, Sample(), null);
        using var wrong = await PostAsync(client, Sample(), "example-invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("Bearer", Assert.Single(wrong.Headers.WwwAuthenticate).Scheme);
        Assert.Equal("application/problem+json", wrong.Content.Headers.ContentType?.MediaType);
        Assert.Equal(0L, await CountAsync());
    }

    [Fact]
    public async Task InvalidJsonAndSchemaViolationsUseDistinctResponses()
    {
        await using var app = new PulseApiFactory(DatabasePath);
        using var client = app.CreateClient();
        var duplicateKey = Sample().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal);
        using var malformed = await PostAsync(client, "{\"privateSentinel\":", PulseApiFactory.AgentToken);
        using var duplicate = await PostAsync(client, duplicateKey, PulseApiFactory.AgentToken);
        using var wrongContentType = await PostAsync(client, Sample(), PulseApiFactory.AgentToken, mediaType: "text/plain");
        using var spoofed = await PostAsync(client, Sample().Insert(1, "\"agentId\":\"demo-b\","), PulseApiFactory.AgentToken);
        using var future = await PostAsync(client, Sample(DateTimeOffset.UtcNow.AddHours(1)), PulseApiFactory.AgentToken);
        using var expired = await PostAsync(client, Sample(DateTimeOffset.UtcNow.AddDays(-8)), PulseApiFactory.AgentToken);
        using var badVersion = await PostAsync(client, Sample().Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal), PulseApiFactory.AgentToken);
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrongContentType.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, spoofed.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, future.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, expired.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badVersion.StatusCode);
        using var badFormat = await PostAsync(client,
            Sample().Replace("\"temperaturesCelsius\":{}", "\"temperaturesCelsius\":{},\"tls\":{\"status\":\"unavailable\",\"observedAt\":\"neverZ\"}", StringComparison.Ordinal),
            PulseApiFactory.AgentToken);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badFormat.StatusCode);
        Assert.DoesNotContain("privateSentinel", await malformed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0L, await CountAsync());
    }

    [Fact]
    public async Task OversizedBodiesAreRejectedEvenWithoutContentLength()
    {
        await using var app = new PulseApiFactory(DatabasePath);
        app.UseKestrel(0);
        using var client = app.CreateClient();
        var payload = Sample().PadRight((64 * 1024) + 1);
        using var fixedLength = await PostAsync(client, payload, PulseApiFactory.AgentToken);
        using var chunked = await PostAsync(client, payload, PulseApiFactory.AgentToken, chunked: true);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, fixedLength.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunked.StatusCode);
        using var exactlyAtLimit = await PostAsync(client, Sample().PadRight(64 * 1024), PulseApiFactory.AgentToken);
        Assert.Equal(HttpStatusCode.NoContent, exactlyAtLimit.StatusCode);
        Assert.Equal(1L, await CountAsync());
    }

    [Fact]
    public async Task ARealSqliteWriteLockReturns503AndTheSameSampleCanBeRetried()
    {
        await using var app = new PulseApiFactory(DatabasePath);
        using var client = app.CreateClient();
        var payload = Sample();

        await using var blocker = PulseApiFactory.Connect(DatabasePath);
        await blocker.OpenAsync();
        using (var writeLock = blocker.BeginTransaction(deferred: false))
        {
            using var failed = await PostAsync(client, payload, PulseApiFactory.AgentToken);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.Equal("application/problem+json", failed.Content.Headers.ContentType?.MediaType);
            Assert.Equal(0L, await CountAsync());
            writeLock.Rollback();
        }

        using var retry = await PostAsync(client, payload, PulseApiFactory.AgentToken);
        Assert.Equal(HttpStatusCode.NoContent, retry.StatusCode);
        Assert.Equal(1L, await CountAsync());
    }

    [Fact]
    public async Task ProductionHealthEndpointsAndDevelopmentOpenApiHaveTheExpectedExposure()
    {
        await using var production = new PulseApiFactory(DatabasePath);
        using var client = production.CreateClient();
        using var live = await client.GetAsync("/health/live");
        using var ready = await client.GetAsync("/health/ready");
        using var docs = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, docs.StatusCode);

        await using var development = new PulseApiFactory(DatabasePath, "Development");
        using var developmentClient = development.CreateClient();
        using var developmentDocs = await developmentClient.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, developmentDocs.StatusCode);
        using var openApi = JsonDocument.Parse(await developmentDocs.Content.ReadAsStringAsync());
        var operation = openApi.RootElement.GetProperty("paths").GetProperty(IngestPath).GetProperty("post");
        Assert.True(operation.GetProperty("requestBody").GetProperty("content").TryGetProperty("application/json", out _));

        await using var database = PulseApiFactory.Connect(DatabasePath);
        await database.OpenAsync();
        await using var cursor = database.CreateCommand();
        cursor.CommandText = "DROP TABLE TelemetrySamples";
        await cursor.ExecuteNonQueryAsync();
        using var liveWithoutTable = await client.GetAsync("/health/live");
        using var readinessWithoutTable = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, liveWithoutTable.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readinessWithoutTable.StatusCode);
    }

    [Fact]
    public async Task TheProductionPythonAgentSendsAValidatedSampleToAMigratedWalDatabase()
    {
        await using var app = new PulseApiFactory(DatabasePath);
        app.UseKestrel(0);
        using var client = app.CreateClient();
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "send_sample.py");
        using var encoder = Process.Start(new ProcessStartInfo("python3")
        {
            ArgumentList = { "-B", "-E", "-s", fixturePath, new Uri(client.BaseAddress!, IngestPath).AbsoluteUri },
            Environment = { ["PULSE_TEST_BEARER"] = PulseApiFactory.AgentToken },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Could not start the Python encoder.");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pythonPayload = encoder.StandardOutput.ReadToEndAsync(deadline.Token);
        var errors = encoder.StandardError.ReadToEndAsync(deadline.Token);
        await encoder.WaitForExitAsync(deadline.Token);
        Assert.True(encoder.ExitCode == 0, await errors);
        var payload = await pythonPayload;
        using var document = JsonDocument.Parse(payload);
        var observedAt = document.RootElement.GetProperty("observedAt").GetDateTimeOffset();
        await using var database = PulseApiFactory.Connect(DatabasePath);
        await database.OpenAsync();
        await using var cursor = database.CreateCommand();
        cursor.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", await cursor.ExecuteScalarAsync());
        cursor.CommandText = "SELECT AgentId, SampleId, ObservedAtUtcTicks, ReceivedAtUtcTicks, Payload FROM TelemetrySamples";
        await using var row = await cursor.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        Assert.Equal("demo-a", row.GetString(0));
        Assert.Equal(document.RootElement.GetProperty("sampleId").GetGuid(), row.GetGuid(1));
        Assert.Equal(observedAt.UtcTicks, row.GetInt64(2));
        Assert.True(row.GetInt64(3) >= observedAt.UtcTicks);
        Assert.Equal(payload, row.GetString(4));
        Assert.False(await row.ReadAsync());
    }

    public void Dispose() => _temporary.Delete(recursive: true);

    private async Task<long> CountAsync()
    {
        await using var database = PulseApiFactory.Connect(DatabasePath);
        await database.OpenAsync();
        await using var cursor = database.CreateCommand();
        cursor.CommandText = "SELECT count(*) FROM TelemetrySamples";
        return Convert.ToInt64(await cursor.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string sample, string? token, bool chunked = false, string mediaType = "application/json")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, IngestPath);
        request.Content = new StringContent(sample, Encoding.UTF8, mediaType);
        request.Headers.TransferEncodingChunked = chunked;
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request);
    }

    private static string Sample(DateTimeOffset? observedAt = null)
    {
        return $$$$"""
            {"schemaVersion":1,"sampleId":"{{{{Guid.NewGuid():D}}}}","observedAt":"{{{{(observedAt ?? DateTimeOffset.UtcNow).UtcDateTime:O}}}}","kind":"host",
            "values":{"bootId":"8efee3f4-a0e6-4517-a139-8abe1252ed19",
            "cpuTimesSeconds":{"user":1,"nice":0,"system":1,"idle":7,"iowait":0,"irq":0,"softirq":0,"steal":0},
            "memory":{"totalBytes":4096,"availableBytes":2048,"swapTotalBytes":0,"swapFreeBytes":0},
            "loadAverage":[0.25,0.5,0.75],"uptimeSeconds":42,"filesystems":{},"network":{},"temperaturesCelsius":{}}}
            """;
    }
}
