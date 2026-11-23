namespace HomeLab.Pulse.Api.Telemetry.Retention;

internal static class RetentionRegistration
{
    public static void AddTelemetryRetention(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RetentionOptions>()
            .Bind(configuration.GetSection(RetentionOptions.Section))
            .Validate(options => options.Days is >= 7 and <= 30, "Retention days must be between 7 and 30.")
            .Validate(options => options.BatchSize is >= 1 and <= 1024, "Retention batch size must be between 1 and 1024.")
            .ValidateOnStart();
        services.AddScoped<RetentionSweep>();
        services.AddHostedService<RetentionWorker>();
    }
}
