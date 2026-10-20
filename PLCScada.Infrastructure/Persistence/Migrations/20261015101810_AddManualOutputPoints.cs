using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PLCScada.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddManualOutputPoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ManualOutputPoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false, collation: "NOCASE"),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultPlcAddress = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    DefaultModbusAddress = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManualOutputPoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ManualOutputPoints_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                WITH Defaults(Name, PlcAddress, ModbusAddress, SortOrder) AS (
                    VALUES
                        ('通电', 'Y0', 24576, 0),
                        ('压头气缸', 'Y1', 24577, 1),
                        ('下压气缸', 'Y2', 24578, 2),
                        ('通电平移', 'Y3', 24579, 3),
                        ('开机气缸', 'Y4', 24580, 4),
                        ('模式切换气缸', 'Y5', 24581, 5),
                        ('灰尘模拟气缸', 'Y6', 24582, 6),
                        ('地刷通电气缸', 'Y7', 24583, 7),
                        ('编码器', 'Y10', 24584, 8),
                        ('扫码启动', 'Y11', 24585, 9),
                        ('打点气缸', 'Y12', 24586, 10),
                        ('相机拍照', 'Y13', 24587, 11),
                        ('相机方案切换', 'Y14', 24588, 12),
                        ('复位', 'M0', 0, 13)
                )
                INSERT INTO ManualOutputPoints
                    (Id, DeviceId, Name, SortOrder, DefaultPlcAddress, DefaultModbusAddress)
                SELECT
                    lower(substr(hex(randomblob(16)), 1, 8) || '-' ||
                          substr(hex(randomblob(16)), 9, 4) || '-' ||
                          substr(hex(randomblob(16)), 13, 4) || '-' ||
                          substr(hex(randomblob(16)), 17, 4) || '-' ||
                          substr(hex(randomblob(16)), 21, 12)),
                    Devices.Id, Defaults.Name, Defaults.SortOrder,
                    Defaults.PlcAddress, Defaults.ModbusAddress
                FROM Devices CROSS JOIN Defaults;
                """);

            migrationBuilder.CreateTable(
                name: "ManualOutputBindings_New",
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
                INSERT INTO ManualOutputBindings_New (DeviceId, OutputPointId, TagId)
                SELECT bindings.DeviceId, points.Id, bindings.TagId
                FROM ManualOutputBindings AS bindings
                INNER JOIN ManualOutputPoints AS points
                    ON points.DeviceId = bindings.DeviceId AND points.Name = bindings.OutputName;
                """);
            migrationBuilder.DropTable(name: "ManualOutputBindings");
            migrationBuilder.RenameTable(name: "ManualOutputBindings_New", newName: "ManualOutputBindings");

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

            migrationBuilder.CreateIndex(
                name: "IX_ManualOutputPoints_DeviceId_Name",
                table: "ManualOutputPoints",
                columns: new[] { "DeviceId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManualOutputPoints_DeviceId_SortOrder",
                table: "ManualOutputPoints",
                columns: new[] { "DeviceId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ManualOutputBindings");

            migrationBuilder.CreateTable(
                name: "ManualOutputBindings_Old",
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
            migrationBuilder.Sql("""
                INSERT INTO ManualOutputBindings_Old (DeviceId, OutputName, TagId)
                SELECT bindings.DeviceId, points.Name, bindings.TagId
                FROM ManualOutputBindings AS bindings
                INNER JOIN ManualOutputPoints AS points
                    ON points.DeviceId = bindings.DeviceId AND points.Id = bindings.OutputPointId;
                """);
            migrationBuilder.RenameTable(name: "ManualOutputBindings_Old", newName: "ManualOutputBindings");
            migrationBuilder.CreateIndex(
                name: "IX_ManualOutputBindings_DeviceId_TagId",
                table: "ManualOutputBindings",
                columns: new[] { "DeviceId", "TagId" },
                unique: true);
            migrationBuilder.CreateIndex(
                name: "IX_ManualOutputBindings_TagId",
                table: "ManualOutputBindings",
                column: "TagId");
            migrationBuilder.DropTable(name: "ManualOutputPoints");
        }
    }
}
