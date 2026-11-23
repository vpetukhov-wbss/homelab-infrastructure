namespace HomeLab.Pulse.Api.Telemetry.Retention;

internal sealed class RetentionOptions
{
    public const string Section = "Telemetry:Retention";

    // The receiver accepts observations up to seven days old.
    public int Days { get; init; } = 7;

    public int BatchSize { get; init; } = 512;
}
