using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VSaga.Dashboard.Identity.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class TemporaryPasswordExpiry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PasswordExpiresAtUtc",
                table: "Users",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PasswordExpiresAtUtc",
                table: "Users");
        }
    }
}
