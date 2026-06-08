using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataSyncManager.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddUpsertWatermark : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SyncOverlapMinutes",
                table: "Jobs",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "MaxSourceTimestamp",
                table: "JobRuns",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SyncWindowStart",
                table: "JobRuns",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SyncOverlapMinutes",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "MaxSourceTimestamp",
                table: "JobRuns");

            migrationBuilder.DropColumn(
                name: "SyncWindowStart",
                table: "JobRuns");
        }
    }
}
