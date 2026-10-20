using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PLCScada.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRecipeRevisionHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecipeVersions",
                columns: table => new
                {
                    RecipeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    DeviceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DeviceName = table.Column<string>(type: "TEXT", nullable: true),
                    DeviceProtocol = table.Column<string>(type: "TEXT", nullable: true),
                    DeviceIpAddress = table.Column<string>(type: "TEXT", nullable: true),
                    DevicePort = table.Column<int>(type: "INTEGER", nullable: true),
                    DeviceUnitId = table.Column<int>(type: "INTEGER", nullable: true),
                    SnapshotSchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    ParametersJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeVersions", x => new { x.RecipeId, x.Revision });
                    table.CheckConstraint("CK_RecipeVersions_PositiveRevision", "Revision > 0 AND SnapshotSchemaVersion > 0");
                    table.ForeignKey(
                        name: "FK_RecipeVersions_Recipes_RecipeId",
                        column: x => x.RecipeId,
                        principalTable: "Recipes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                INSERT INTO RecipeVersions (
                    RecipeId, Revision, CreatedAt, Name, Description, DeviceId,
                    DeviceName, DeviceProtocol, DeviceIpAddress, DevicePort, DeviceUnitId,
                    SnapshotSchemaVersion, ParametersJson)
                SELECT
                    recipe.Id,
                    recipe.Revision,
                    recipe.UpdatedAt,
                    recipe.Name,
                    recipe.Description,
                    recipe.DeviceId,
                    device.Name,
                    device.Protocol,
                    device.IpAddress,
                    device.Port,
                    device.UnitId,
                    1,
                    COALESCE((
                        SELECT json_group_array(json_object(
                            'TagId', tag.Id,
                            'TagName', tag.Name,
                            'DeviceId', tag.DeviceId,
                            'AddressType', tag.AddressType,
                            'Address', tag.Address,
                            'DataType', tag.DataType,
                            'AccessMode', tag.AccessMode,
                            'Enabled', json(CASE WHEN tag.Enabled <> 0 THEN 'true' ELSE 'false' END),
                            'Unit', tag.Unit,
                            'Scale', tag.Scale,
                            'Offset', tag.Offset,
                            'ByteOrder', tag.ByteOrder,
                            'WordOrder', tag.WordOrder,
                            'MinValue', tag.MinValue,
                            'MaxValue', tag.MaxValue,
                            'EngineeringValue', parameter.EngineeringValue,
                            'BooleanValue', json(CASE
                                WHEN parameter.BooleanValue IS NULL THEN 'null'
                                WHEN parameter.BooleanValue <> 0 THEN 'true'
                                ELSE 'false'
                            END)
                        ))
                        FROM RecipeParameters AS parameter
                        INNER JOIN Tags AS tag ON tag.Id = parameter.TagId
                        WHERE parameter.RecipeId = recipe.Id
                    ), '[]')
                FROM Recipes AS recipe
                LEFT JOIN Devices AS device ON device.Id = recipe.DeviceId
                WHERE recipe.Revision > 0;
                """);

            migrationBuilder.Sql("""
                CREATE TRIGGER TR_RecipeVersions_ImmutableUpdate
                BEFORE UPDATE ON RecipeVersions
                BEGIN
                    SELECT RAISE(ABORT, 'Recipe versions are append-only');
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecipeVersions");
        }
    }
}
