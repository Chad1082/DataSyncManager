using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataSyncManager.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectScheduleTimezone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ScheduleTimezone",
                table: "Projects",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScheduleTimezone",
                table: "Projects");
        }
    }
}
