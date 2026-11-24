namespace HomeLab.Pulse.Api.Dashboard.Signals;

internal sealed class HealthSignalOptions
{
    public const string Section = "Dashboard:Signals";

    public TimeSpan SustainedWindow { get; set; } = TimeSpan.FromMinutes(5);

    public double CpuUsagePercent { get; set; } = 85;

    public double MemoryUsagePercent { get; set; } = 90;

    public double DiskUsagePercent { get; set; } = 80;

    public double TemperatureCelsius { get; set; } = 85;

    public double NginxServerErrorPercent { get; set; } = 5;

    public long GiteaResponseTimeMs { get; set; } = 1_000;

    public long CiWaitingJobs { get; set; } = 7;

    public long RabbitReadyMessages { get; set; } = 100;

    public int TlsExpiryDays { get; set; } = 14;
}
