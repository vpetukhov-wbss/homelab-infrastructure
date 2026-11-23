using System.Text.Json;
using Json.Schema;

namespace HomeLab.Pulse.Api.Telemetry.Ingest;

internal sealed class TelemetryValidator
{
    private readonly JsonSchema _schema;

    public TelemetryValidator()
    {
        using var stream = typeof(TelemetryValidator).Assembly.GetManifestResourceStream("Pulse.Telemetry.V1")
            ?? throw new InvalidOperationException("The telemetry v1 schema is missing.");
        using var reader = new StreamReader(stream);
        _schema = JsonSchema.FromText(reader.ReadToEnd());
    }

    public bool TryReadSample(
        JsonElement root, DateTimeOffset receivedAt, out Guid sampleId, out DateTimeOffset observedAt)
    {
        sampleId = default;
        observedAt = default;

        return _schema.Evaluate(root, new EvaluationOptions { RequireFormatValidation = true }).IsValid
            && root.GetProperty("sampleId").TryGetGuid(out sampleId)
            && root.GetProperty("observedAt").TryGetDateTimeOffset(out observedAt)
            && observedAt >= receivedAt.AddDays(-7)
            && observedAt <= receivedAt.AddMinutes(5);
    }
}
