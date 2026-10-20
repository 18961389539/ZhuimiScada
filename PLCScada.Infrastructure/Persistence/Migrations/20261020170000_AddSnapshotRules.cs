using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PLCScada.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSnapshotRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SnapshotRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RuleName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    OccurredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Sn = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ValuesJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnapshotRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SnapshotRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false, collation: "NOCASE"),
                    TriggerTagId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnapshotRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SnapshotRules_Tags_TriggerTagId",
                        column: x => x.TriggerTagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SnapshotRulePoints",
                columns: table => new
                {
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TagId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnapshotRulePoints", x => new { x.RuleId, x.TagId });
                    table.ForeignKey(
                        name: "FK_SnapshotRulePoints_SnapshotRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "SnapshotRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SnapshotRulePoints_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotRecords_OccurredAt",
                table: "SnapshotRecords",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotRecords_Sn",
                table: "SnapshotRecords",
                column: "Sn");

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotRulePoints_TagId",
                table: "SnapshotRulePoints",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotRules_Name",
                table: "SnapshotRules",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotRules_TriggerTagId",
                table: "SnapshotRules",
                column: "TriggerTagId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SnapshotRecords");

            migrationBuilder.DropTable(
                name: "SnapshotRulePoints");

            migrationBuilder.DropTable(
                name: "SnapshotRules");
        }
    }
}
