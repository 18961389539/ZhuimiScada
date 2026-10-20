using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PLCScada.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ScadaDbContext))]
[Migration("20261016120000_AddManualOutputMonitorTag")]
public sealed class AddManualOutputMonitorTag : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ManualOutputBindings_New",
            columns: table => new
            {
                DeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                OutputPointId = table.Column<Guid>(type: "TEXT", nullable: false),
                TagId = table.Column<Guid>(type: "TEXT", nullable: true),
                MonitorTagId = table.Column<Guid>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ManualOutputBindings", x => new { x.DeviceId, x.OutputPointId });
                table.ForeignKey(
                    name: "FK_ManualOutputBindings_Devices_DeviceId",
                    column: x => x.DeviceId,
                    principalTable: "Devices",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_ManualOutputBindings_ManualOutputPoints_OutputPointId",
                    column: x => x.OutputPointId,
                    principalTable: "ManualOutputPoints",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_ManualOutputBindings_Tags_MonitorTagId",
                    column: x => x.MonitorTagId,
                    principalTable: "Tags",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_ManualOutputBindings_Tags_TagId",
                    column: x => x.TagId,
                    principalTable: "Tags",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.Sql("""
            INSERT INTO ManualOutputBindings_New (DeviceId, OutputPointId, TagId, MonitorTagId)
            SELECT DeviceId, OutputPointId, TagId, NULL FROM ManualOutputBindings;
            """);
        migrationBuilder.DropTable(name: "ManualOutputBindings");
        migrationBuilder.RenameTable(name: "ManualOutputBindings_New", newName: "ManualOutputBindings");
        CreateIndexes(migrationBuilder);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ManualOutputBindings_Old",
            columns: table => new
            {
                DeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                OutputPointId = table.Column<Guid>(type: "TEXT", nullable: false),
                TagId = table.Column<Guid>(type: "TEXT", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ManualOutputBindings", x => new { x.DeviceId, x.OutputPointId });
                table.ForeignKey(
                    name: "FK_ManualOutputBindings_Devices_DeviceId",
                    column: x => x.DeviceId,
                    principalTable: "Devices",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_ManualOutputBindings_ManualOutputPoints_OutputPointId",
                    column: x => x.OutputPointId,
                    principalTable: "ManualOutputPoints",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_ManualOutputBindings_Tags_TagId",
                    column: x => x.TagId,
                    principalTable: "Tags",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.Sql("""
            INSERT INTO ManualOutputBindings_Old (DeviceId, OutputPointId, TagId)
            SELECT DeviceId, OutputPointId, TagId FROM ManualOutputBindings;
            """);
        migrationBuilder.DropTable(name: "ManualOutputBindings");
        migrationBuilder.RenameTable(name: "ManualOutputBindings_Old", newName: "ManualOutputBindings");
        migrationBuilder.CreateIndex(
            name: "IX_ManualOutputBindings_DeviceId_TagId",
            table: "ManualOutputBindings",
            columns: new[] { "DeviceId", "TagId" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_ManualOutputBindings_OutputPointId",
            table: "ManualOutputBindings",
            column: "OutputPointId");
        migrationBuilder.CreateIndex(
            name: "IX_ManualOutputBindings_TagId",
            table: "ManualOutputBindings",
            column: "TagId");
    }

    private static void CreateIndexes(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_ManualOutputBindings_DeviceId_MonitorTagId",
            table: "ManualOutputBindings",
            columns: new[] { "DeviceId", "MonitorTagId" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_ManualOutputBindings_DeviceId_TagId",
            table: "ManualOutputBindings",
            columns: new[] { "DeviceId", "TagId" },
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_ManualOutputBindings_MonitorTagId",
            table: "ManualOutputBindings",
            column: "MonitorTagId");
        migrationBuilder.CreateIndex(
            name: "IX_ManualOutputBindings_OutputPointId",
            table: "ManualOutputBindings",
            column: "OutputPointId");
        migrationBuilder.CreateIndex(
            name: "IX_ManualOutputBindings_TagId",
            table: "ManualOutputBindings",
            column: "TagId");
    }
}
