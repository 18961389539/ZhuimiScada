using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PLCScada.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTagAlarmLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "AlarmDeadband",
                table: "Tags",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<double>(
                name: "AlarmHigh",
                table: "Tags",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "AlarmLow",
                table: "Tags",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AlarmDeadband",
                table: "Tags");

            migrationBuilder.DropColumn(
                name: "AlarmHigh",
                table: "Tags");

            migrationBuilder.DropColumn(
                name: "AlarmLow",
                table: "Tags");
        }
    }
}
