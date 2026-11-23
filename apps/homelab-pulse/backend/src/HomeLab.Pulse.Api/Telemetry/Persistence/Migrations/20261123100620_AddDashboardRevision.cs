using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HomeLab.Pulse.Api.Telemetry.Persistence.Migrations;

/// <inheritdoc />
public partial class AddDashboardRevision : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "DashboardRevision",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Value = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DashboardRevision", x => x.Id);
            });

        migrationBuilder.InsertData(
            table: "DashboardRevision",
            columns: ["Id", "Value"],
            values: [1, 0L]);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "DashboardRevision");
    }
}
