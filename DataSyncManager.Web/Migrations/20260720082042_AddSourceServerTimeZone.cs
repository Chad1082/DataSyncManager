using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataSyncManager.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceServerTimeZone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceTimeZone",
                table: "SourceServers",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceTimeZone",
                table: "SourceServers");
        }
    }
}
