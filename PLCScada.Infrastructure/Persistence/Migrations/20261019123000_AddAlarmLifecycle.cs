using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PLCScada.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAlarmLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AcknowledgeNote",
                table: "AlarmRecords",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ClearedAt",
                table: "AlarmRecords",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "AlarmRecords",
                type: "INTEGER",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<int>(
                name: "RepeatCount",
                table: "AlarmRecords",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            // 存量报警按严重度回填优先级：Error→P1、Warning→P2、Info→P3
            migrationBuilder.Sql(
                "UPDATE AlarmRecords SET Priority = CASE Severity " +
                "WHEN 'Error' THEN 1 WHEN 'Warning' THEN 2 WHEN 'Info' THEN 3 ELSE 2 END;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcknowledgeNote",
                table: "AlarmRecords");

            migrationBuilder.DropColumn(
                name: "ClearedAt",
                table: "AlarmRecords");

            migrationBuilder.DropColumn(
                name: "Priority",
                table: "AlarmRecords");

            migrationBuilder.DropColumn(
                name: "RepeatCount",
                table: "AlarmRecords");
        }
    }
}
