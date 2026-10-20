using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PLCScada.Infrastructure.Persistence.Migrations
{
    public partial class AddManualOutputBindings : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ManualOutputBindings",
                columns: table => new
                {
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OutputName = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    TagId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualOutputBindings", x => new { x.DeviceId, x.OutputName });
                    table.ForeignKey(
                        name: "FK_ManualOutputBindings_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ManualOutputBindings_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ManualOutputBindings_DeviceId_TagId",
                table: "ManualOutputBindings",
                columns: new[] { "DeviceId", "TagId" },
                unique: true);
            migrationBuilder.CreateIndex(
                name: "IX_ManualOutputBindings_TagId",
                table: "ManualOutputBindings",
                column: "TagId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.DropTable(name: "ManualOutputBindings");
    }
}
