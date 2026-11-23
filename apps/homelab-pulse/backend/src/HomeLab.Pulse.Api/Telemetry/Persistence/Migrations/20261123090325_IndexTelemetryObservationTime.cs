using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeLab.Pulse.Api.Telemetry.Persistence.Migrations;

/// <inheritdoc />
public partial class IndexTelemetryObservationTime : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_TelemetrySamples_ObservedAtUtcTicks",
            table: "TelemetrySamples",
            column: "ObservedAtUtcTicks");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_TelemetrySamples_ObservedAtUtcTicks",
            table: "TelemetrySamples");
    }
}
