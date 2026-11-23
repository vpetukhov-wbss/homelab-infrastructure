using System.Diagnostics;
using System.Globalization;
using Xunit;

namespace HomeLab.Pulse.Api.IntegrationTests;

internal static class PythonAgentFixture
{
    public static async Task<string> SendAsync(Uri baseAddress, CancellationToken cancellationToken, DateTimeOffset? observedAt = null)
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "send_sample.py");
        var startInfo = new ProcessStartInfo("python3")
        {
            ArgumentList = { "-B", "-E", "-s", fixturePath, new Uri(baseAddress, "/internal/v1/telemetry").AbsoluteUri },
            Environment = { ["PULSE_TEST_BEARER"] = PulseApiFactory.AgentToken },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (observedAt is { } instant)
        {
            startInfo.ArgumentList.Add(instant.ToString("O", CultureInfo.InvariantCulture));
        }

        using var encoder = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the Python encoder.");
        var payload = encoder.StandardOutput.ReadToEndAsync(cancellationToken);
        var errors = encoder.StandardError.ReadToEndAsync(cancellationToken);
        await encoder.WaitForExitAsync(cancellationToken);
        Assert.True(encoder.ExitCode == 0, await errors);
        return await payload;
    }
}
