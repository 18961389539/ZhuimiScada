using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PLCScada.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSnRecordDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeviceName",
                table: "SnRecords",
                type: "TEXT",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "SnRecords",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecipeName",
                table: "SnRecords",
                type: "TEXT",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RecipeVersion",
                table: "SnRecords",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeviceName",
                table: "SnRecords");

            migrationBuilder.DropColumn(
                name: "Note",
                table: "SnRecords");

            migrationBuilder.DropColumn(
                name: "RecipeName",
                table: "SnRecords");

            migrationBuilder.DropColumn(
                name: "RecipeVersion",
                table: "SnRecords");
        }
    }
}
