using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PLCScada.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSnapshotPointMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Alias",
                table: "SnapshotRulePoints",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Decimals",
                table: "SnapshotRulePoints",
                type: "INTEGER",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<bool>(
                name: "Required",
                table: "SnapshotRulePoints",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "SnapshotRulePoints",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Unit",
                table: "SnapshotRulePoints",
                type: "TEXT",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Alias",
                table: "SnapshotRulePoints");

            migrationBuilder.DropColumn(
                name: "Decimals",
                table: "SnapshotRulePoints");

            migrationBuilder.DropColumn(
                name: "Required",
                table: "SnapshotRulePoints");

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "SnapshotRulePoints");

            migrationBuilder.DropColumn(
                name: "Unit",
                table: "SnapshotRulePoints");
        }
    }
}
