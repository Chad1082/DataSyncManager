using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataSyncManager.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    SmtpHost = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    SmtpPort = table.Column<int>(type: "int", nullable: false),
                    SmtpUser = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    SmtpPass = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    FromAddress = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: false),
                    FromName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    UseSsl = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailSettings");
        }
    }
}
