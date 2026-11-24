using HomeLab.Pulse.Api.Dashboard;
using HomeLab.Pulse.Api.Dashboard.History;
using HomeLab.Pulse.Api.Dashboard.Machines;
using HomeLab.Pulse.Api.Dashboard.Overview;
using HomeLab.Pulse.Api.Dashboard.Realtime;
using HomeLab.Pulse.Api.Dashboard.Services;
using HomeLab.Pulse.Api.Dashboard.Signals;
using HomeLab.Pulse.Api.Hosting;
using HomeLab.Pulse.Api.Telemetry.Ingest;
using HomeLab.Pulse.Api.Telemetry.Persistence;
using HomeLab.Pulse.Api.Telemetry.Retention;
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
builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = 4096);
builder.Services.AddSingleton<PulseBroadcastWorker>();
builder.Services.AddHostedService<PulseBroadcastWorker>(services => services.GetRequiredService<PulseBroadcastWorker>());
builder.Services.AddSingleton(services => new TelemetryStorageSettings(Path.GetFullPath(
    services.GetRequiredService<IConfiguration>()["Telemetry:DatabasePath"] ?? "data/pulse.db",
    services.GetRequiredService<IWebHostEnvironment>().ContentRootPath)));
builder.Services.AddDbContext<TelemetryDbContext>((services, options) =>
    options.UseSqlite(TelemetryDatabase.ConnectionString(services.GetRequiredService<TelemetryStorageSettings>().DatabasePath)));
builder.Services.AddTelemetryRetention(builder.Configuration);
builder.Services.AddSingleton<PublicCatalog>();
builder.Services.AddOptions<HealthSignalOptions>()
    .Bind(builder.Configuration.GetSection(HealthSignalOptions.Section))
    .Validate(options => options.SustainedWindow is { } window
        && window >= TimeSpan.FromMinutes(1) && window <= TimeSpan.FromHours(1),
        "Dashboard signal sustained window must be between one minute and one hour.")
    .Validate(options => options.CpuUsagePercent is > 0 and <= 100
        && options.MemoryUsagePercent is > 0 and <= 100
        && options.DiskUsagePercent is > 0 and <= 100
        && options.TemperatureCelsius is > 0 and <= 150
        && options.NginxServerErrorPercent is > 0 and <= 100,
        "Dashboard percentage and temperature signal thresholds are outside their safe ranges.")
    .Validate(options => options.GiteaResponseTimeMs > 0 && options.CiWaitingJobs > 0
        && options.RabbitReadyMessages > 0 && options.TlsExpiryDays > 0,
        "Dashboard count and duration signal thresholds must be positive.")
    .ValidateOnStart();
builder.Services.AddSingleton<NodeHealthSignals>();
builder.Services.AddSingleton<ServiceHealthSignals>();
builder.Services.AddSingleton<OverviewPublication>();
builder.Services.AddScoped<OverviewProjection>();
builder.Services.AddScoped<DashboardRevisionStore>();
builder.Services.AddHostedService<OverviewWorker>();
builder.Services.AddSingleton<ApprovedHardware>();
builder.Services.AddSingleton<MachineCatalog>();
builder.Services.AddSingleton<MachinePublication>();
builder.Services.AddScoped<MachineProjection>();
builder.Services.AddScoped<SmartProjection>();
builder.Services.AddHostedService<MachineWorker>();
builder.Services.AddSingleton<ServiceCatalog>();
builder.Services.AddSingleton<ServicesPublication>();
builder.Services.AddScoped<ServiceObservations>();
builder.Services.AddScoped<ServicesProjection>();
builder.Services.AddHostedService<ServicesWorker>();
builder.Services.AddSingleton<HistoryPublication>();
builder.Services.AddScoped<HistoryProjection>();
builder.Services.AddHostedService<HistoryWorker>();
builder.Services.AddHealthChecks().AddCheck<TelemetryReadiness>("telemetry-storage", tags: ["ready"]);

var app = builder.Build();

_ = app.Services.GetRequiredService<AgentCredentials>();
_ = app.Services.GetRequiredService<TelemetryValidator>();
_ = app.Services.GetRequiredService<MachineCatalog>();
_ = app.Services.GetRequiredService<ServiceCatalog>();
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
app.MapDashboardOverview();
app.MapDashboardMachine();
app.MapDashboardServices();
app.MapDashboardHistory();
app.MapHub<PulseHub>("/hubs/pulse").AllowAnonymous();

await app.RunAsync();
