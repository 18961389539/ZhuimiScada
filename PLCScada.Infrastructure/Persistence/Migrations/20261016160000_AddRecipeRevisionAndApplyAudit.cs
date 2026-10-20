using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PLCScada.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ScadaDbContext))]
[Migration("20261016160000_AddRecipeRevisionAndApplyAudit")]
public sealed class AddRecipeRevisionAndApplyAudit : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "Revision",
            table: "Recipes",
            type: "INTEGER",
            nullable: false,
            defaultValue: 1);
        migrationBuilder.AddColumn<long>(
            name: "LastAppliedAt",
            table: "Recipes",
            type: "INTEGER",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "Revision", table: "Recipes");
        migrationBuilder.DropColumn(name: "LastAppliedAt", table: "Recipes");
    }
}
