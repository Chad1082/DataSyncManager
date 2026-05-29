using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataSyncManager.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddJobSyncStartDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SyncStartDate",
                table: "Jobs",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SyncStartDate",
                table: "Jobs");
        }
    }
}
