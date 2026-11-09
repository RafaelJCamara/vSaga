using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VSaga.Dashboard.Identity.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class AddUserDisplayPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisplayPreferences",
                table: "Users",
                type: "TEXT",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DisplayPreferences",
                table: "Users");
        }
    }
}
