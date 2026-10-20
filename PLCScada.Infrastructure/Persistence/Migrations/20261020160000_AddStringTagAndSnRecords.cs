using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PLCScada.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStringTagAndSnRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "StringEncoding",
                table: "Tags",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "StringRegisters",
                table: "Tags",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "SnRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sn = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CapturedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SnRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SnRecords_CapturedAt",
                table: "SnRecords",
                column: "CapturedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SnRecords_Sn",
                table: "SnRecords",
                column: "Sn");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SnRecords");

            migrationBuilder.DropColumn(
                name: "StringEncoding",
                table: "Tags");

            migrationBuilder.DropColumn(
                name: "StringRegisters",
                table: "Tags");
        }
    }
}
