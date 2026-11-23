using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeLab.Pulse.Api.Telemetry.Persistence.Migrations;

/// <inheritdoc />
public partial class InitialTelemetry : Migration
{
    private static readonly string[] ObservationIndexColumns = ["AgentId", "ObservedAtUtcTicks"];

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TelemetrySamples",
            columns: table => new
            {
                AgentId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                SampleId = table.Column<Guid>(type: "TEXT", nullable: false),
                ObservedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                ReceivedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                Payload = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TelemetrySamples", x => new { x.AgentId, x.SampleId });
            });

        migrationBuilder.CreateIndex(
            name: "IX_TelemetrySamples_AgentId_ObservedAtUtcTicks",
            table: "TelemetrySamples",
            columns: ObservationIndexColumns);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "TelemetrySamples");
    }
}
