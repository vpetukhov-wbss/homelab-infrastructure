using Microsoft.EntityFrameworkCore;

namespace HomeLab.Pulse.Api.Telemetry.Persistence;

internal sealed class TelemetryDbContext(DbContextOptions<TelemetryDbContext> options) : DbContext(options)
{
    public DbSet<TelemetrySample> Samples => Set<TelemetrySample>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var sample = modelBuilder.Entity<TelemetrySample>();
        sample.ToTable("TelemetrySamples");
        sample.HasKey(item => new { item.AgentId, item.SampleId });
        sample.Property(item => item.AgentId).HasMaxLength(32);
        sample.HasIndex(item => new { item.AgentId, item.ObservedAtUtcTicks });
        sample.HasIndex(item => item.ObservedAtUtcTicks);

        var revision = modelBuilder.Entity<DashboardRevision>();
        revision.ToTable("DashboardRevision");
        revision.HasKey(item => item.Id);
        revision.HasData(new DashboardRevision { Id = 1, Value = 0 });
    }
}
