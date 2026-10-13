using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VSaga.Persistence.EFCore.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AddSagaInstanceSagaTypeUpdatedAtUtcIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_SagaInstances_SagaType_UpdatedAtUtc",
                table: "SagaInstances",
                columns: new[] { "SagaType", "UpdatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SagaInstances_SagaType_UpdatedAtUtc",
                table: "SagaInstances");
        }
    }
}
