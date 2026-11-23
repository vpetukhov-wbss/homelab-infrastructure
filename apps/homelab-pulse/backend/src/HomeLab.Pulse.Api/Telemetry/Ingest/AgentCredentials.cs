using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HomeLab.Pulse.Api.Telemetry.Ingest;

internal sealed partial class AgentCredentials
{
    private readonly (string AgentId, byte[] Digest)[] _agents;

    public AgentCredentials(IConfiguration configuration)
    {
        _agents = configuration.GetSection("Telemetry:Agents").GetChildren().Select(ReadAgent).ToArray();
        if (_agents.Select(agent => Convert.ToHexString(agent.Digest)).Distinct(StringComparer.Ordinal).Count()
            != _agents.Length)
        {
            throw new InvalidOperationException("Agent credential digests must be unique.");
        }
    }

    public string? Authenticate(string token)
    {
        var digest = SHA256.HashData(Encoding.ASCII.GetBytes(token));
        string? authenticatedAgent = null;
        foreach (var agent in _agents)
        {
            if (CryptographicOperations.FixedTimeEquals(digest, agent.Digest))
            {
                authenticatedAgent = agent.AgentId;
            }
        }

        return authenticatedAgent;
    }

    private static (string AgentId, byte[] Digest) ReadAgent(IConfigurationSection section)
    {
        if (!AgentIdPattern().IsMatch(section.Key)
            || section.Value is not { Length: 64 } digest
            || !digest.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("Each agent must have a valid alias and a SHA-256 credential digest.");
        }

        return (section.Key, Convert.FromHexString(digest));
    }

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,31}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex AgentIdPattern();
}
