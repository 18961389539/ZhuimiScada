using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PLCScada.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ScadaDbContext))]
[Migration("20261016140000_AddBooleanRecipeParameters")]
public sealed class AddBooleanRecipeParameters : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "RecipeParameters_New",
            columns: table => new
            {
                RecipeId = table.Column<Guid>(type: "TEXT", nullable: false),
                TagId = table.Column<Guid>(type: "TEXT", nullable: false),
                EngineeringValue = table.Column<double>(type: "REAL", nullable: true),
                BooleanValue = table.Column<bool>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RecipeParameters", x => new { x.RecipeId, x.TagId });
                table.CheckConstraint(
                    "CK_RecipeParameters_OneValue",
                    "(EngineeringValue IS NOT NULL AND BooleanValue IS NULL) OR " +
                    "(EngineeringValue IS NULL AND BooleanValue IS NOT NULL)");
                table.ForeignKey(
                    name: "FK_RecipeParameters_Recipes_RecipeId",
                    column: x => x.RecipeId,
                    principalTable: "Recipes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_RecipeParameters_Tags_TagId",
                    column: x => x.TagId,
                    principalTable: "Tags",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.Sql("""
            INSERT INTO RecipeParameters_New (RecipeId, TagId, EngineeringValue, BooleanValue)
            SELECT RecipeId, TagId, EngineeringValue, NULL FROM RecipeParameters;
            """);
        migrationBuilder.DropTable(name: "RecipeParameters");
        migrationBuilder.RenameTable(name: "RecipeParameters_New", newName: "RecipeParameters");
        migrationBuilder.CreateIndex(
            name: "IX_RecipeParameters_TagId",
            table: "RecipeParameters",
            column: "TagId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "RecipeParameters_Old",
            columns: table => new
            {
                RecipeId = table.Column<Guid>(type: "TEXT", nullable: false),
                TagId = table.Column<Guid>(type: "TEXT", nullable: false),
                EngineeringValue = table.Column<double>(type: "REAL", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RecipeParameters", x => new { x.RecipeId, x.TagId });
                table.ForeignKey(
                    name: "FK_RecipeParameters_Recipes_RecipeId",
                    column: x => x.RecipeId,
                    principalTable: "Recipes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_RecipeParameters_Tags_TagId",
                    column: x => x.TagId,
                    principalTable: "Tags",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.Sql("""
            INSERT INTO RecipeParameters_Old (RecipeId, TagId, EngineeringValue)
            SELECT RecipeId, TagId,
                   COALESCE(EngineeringValue, CASE WHEN BooleanValue = 1 THEN 1.0 ELSE 0.0 END)
            FROM RecipeParameters;
            """);
        migrationBuilder.DropTable(name: "RecipeParameters");
        migrationBuilder.RenameTable(name: "RecipeParameters_Old", newName: "RecipeParameters");
        migrationBuilder.CreateIndex(
            name: "IX_RecipeParameters_TagId",
            table: "RecipeParameters",
            column: "TagId");
    }
}
