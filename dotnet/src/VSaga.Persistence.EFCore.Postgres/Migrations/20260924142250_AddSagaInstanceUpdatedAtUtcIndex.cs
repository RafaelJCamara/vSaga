using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VSaga.Persistence.EFCore.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSagaInstanceUpdatedAtUtcIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_SagaInstances_UpdatedAtUtc",
                table: "SagaInstances",
                column: "UpdatedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SagaInstances_UpdatedAtUtc",
                table: "SagaInstances");
        }
    }
}
