using System.Text.Json;
using HomeLab.Pulse.Api.Telemetry.Persistence;

namespace HomeLab.Pulse.Api.Telemetry.Ingest;

internal sealed class IngestHandler(TelemetryValidator validator, ITelemetryStore store, TimeProvider clock)
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowDuplicateProperties = false,
        MaxDepth = 32,
    };

    public async Task<IngestResult> HandleAsync(string agentId, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        var receivedAt = clock.GetUtcNow();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body, JsonOptions);
        }
        catch (JsonException)
        {
            return IngestResult.InvalidJson;
        }

        using (document)
        {
            if (!validator.TryReadSample(document.RootElement, receivedAt, out var sampleId, out var observedAt))
            {
                return IngestResult.InvalidSample;
            }

            var sample = new TelemetrySample(
                agentId, sampleId, observedAt.UtcTicks, receivedAt.UtcTicks, document.RootElement.GetRawText());
            return await store.TrySaveAsync(sample, cancellationToken)
                ? IngestResult.Accepted
                : IngestResult.StorageUnavailable;
        }
    }
}
