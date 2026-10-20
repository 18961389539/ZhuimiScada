using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PLCScada.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ScadaDbContext))]
[Migration("20261016150000_ScopeRecipesToDevice")]
public sealed class ScopeRecipesToDevice : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "DeviceId",
            table: "Recipes",
            type: "TEXT",
            nullable: true);

        migrationBuilder.Sql("""
            UPDATE Recipes
            SET DeviceId = (
                SELECT Tags.DeviceId
                FROM RecipeParameters
                INNER JOIN Tags ON Tags.Id = RecipeParameters.TagId
                WHERE RecipeParameters.RecipeId = Recipes.Id
                ORDER BY RecipeParameters.TagId
                LIMIT 1
            );
            """);

        migrationBuilder.CreateIndex(
            name: "IX_Recipes_DeviceId",
            table: "Recipes",
            column: "DeviceId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Recipes_DeviceId",
            table: "Recipes");
        migrationBuilder.DropColumn(
            name: "DeviceId",
            table: "Recipes");
    }
}
