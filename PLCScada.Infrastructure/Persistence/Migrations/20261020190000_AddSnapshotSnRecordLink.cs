using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PLCScada.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSnapshotSnRecordLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SnRecordId",
                table: "SnapshotRecords",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SnapshotRecords_SnRecordId",
                table: "SnapshotRecords",
                column: "SnRecordId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SnapshotRecords_SnRecordId",
                table: "SnapshotRecords");

            migrationBuilder.DropColumn(
                name: "SnRecordId",
                table: "SnapshotRecords");
        }
    }
}
