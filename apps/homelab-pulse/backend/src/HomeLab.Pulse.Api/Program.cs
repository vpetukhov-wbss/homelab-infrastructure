using HomeLab.Pulse.Api.Hosting;
using HomeLab.Pulse.Api.Telemetry.Ingest;
using HomeLab.Pulse.Api.Telemetry.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = IngestEndpoint.MaxBodyBytes);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AgentCredentials>();
builder.Services.AddSingleton<TelemetryValidator>();
builder.Services.AddScoped<IngestHandler>();
builder.Services.AddScoped<ITelemetryStore, SqliteTelemetryStore>();
builder.Services.AddAuthentication(AgentAuthenticationHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, AgentAuthenticationHandler>(
        AgentAuthenticationHandler.SchemeName, configureOptions: null);
builder.Services.AddAuthorization();
builder.Services.AddSingleton(services => new TelemetryStorageSettings(Path.GetFullPath(
    services.GetRequiredService<IConfiguration>()["Telemetry:DatabasePath"] ?? "data/pulse.db",
    services.GetRequiredService<IWebHostEnvironment>().ContentRootPath)));
builder.Services.AddDbContext<TelemetryDbContext>((services, options) =>
    options.UseSqlite(TelemetryDatabase.ConnectionString(services.GetRequiredService<TelemetryStorageSettings>().DatabasePath)));
builder.Services.AddHealthChecks().AddCheck<TelemetryReadiness>("telemetry-storage", tags: ["ready"]);

var app = builder.Build();

_ = app.Services.GetRequiredService<AgentCredentials>();
_ = app.Services.GetRequiredService<TelemetryValidator>();
await TelemetryDatabase.InitializeAsync(app.Services, app.Lifetime.ApplicationStopping);

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapTelemetryIngest();

await app.RunAsync();
