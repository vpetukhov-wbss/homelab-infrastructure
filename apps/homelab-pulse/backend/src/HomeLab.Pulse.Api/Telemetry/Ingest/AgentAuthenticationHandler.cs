using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace HomeLab.Pulse.Api.Telemetry.Ingest;

internal sealed class AgentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AgentCredentials credentials)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "PulseAgent";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization;
        if (authorization.Count == 0)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (authorization.Count != 1
            || !AuthenticationHeaderValue.TryParse(authorization[0], out var header)
            || !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || header.Parameter is not { Length: > 0 and <= 512 } token
            || token.Any(static character => character is < '!' or > '~')
            || credentials.Authenticate(token) is not { } agentId)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid agent credential."));
        }

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, agentId)], SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer";
        await TypedResults.Problem(
            title: "A valid agent credential is required.",
            statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(Context);
    }
}
