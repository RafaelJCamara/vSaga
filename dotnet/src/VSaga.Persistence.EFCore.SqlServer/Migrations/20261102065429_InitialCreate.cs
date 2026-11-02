using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VSaga.Persistence.EFCore.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SagaConsumerRegistrations",
                columns: table => new
                {
                    ServiceName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MessageType = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    QueueName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SagaConsumerRegistrations", x => new { x.ServiceName, x.MessageType });
                });

            migrationBuilder.CreateTable(
                name: "SagaEventLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SagaType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    EntryType = table.Column<int>(type: "int", nullable: false),
                    FromState = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ToState = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    MessageType = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    MessageId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TraceId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SpanId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SourceService = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    DestinationService = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    CausationId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SagaEventLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SagaInstances",
                columns: table => new
                {
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SagaType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    CurrentState = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    DataJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ParentSagaType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    ParentCorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BusinessKey = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SagaInstances", x => new { x.SagaType, x.CorrelationId });
                });

            migrationBuilder.CreateTable(
                name: "SagaOutboxMessages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SagaType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MessageId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MessageTypeName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Body = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    Destination = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    HeadersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SagaOutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SagaTimeouts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CorrelationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SagaType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ForState = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DueAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SagaTimeouts", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SagaConsumerRegistrations_MessageType",
                table: "SagaConsumerRegistrations",
                column: "MessageType");

            migrationBuilder.CreateIndex(
                name: "IX_SagaEventLog_SagaType_CorrelationId_Id",
                table: "SagaEventLog",
                columns: new[] { "SagaType", "CorrelationId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SagaEventLog_SagaType_CorrelationId_MessageId",
                table: "SagaEventLog",
                columns: new[] { "SagaType", "CorrelationId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_SagaInstances_CorrelationId",
                table: "SagaInstances",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_SagaInstances_ParentSagaType_ParentCorrelationId",
                table: "SagaInstances",
                columns: new[] { "ParentSagaType", "ParentCorrelationId" });

            migrationBuilder.CreateIndex(
                name: "IX_SagaInstances_SagaType_BusinessKey",
                table: "SagaInstances",
                columns: new[] { "SagaType", "BusinessKey" },
                unique: true,
                filter: "\"BusinessKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SagaInstances_SagaType_Status",
                table: "SagaInstances",
                columns: new[] { "SagaType", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SagaInstances_SagaType_UpdatedAtUtc",
                table: "SagaInstances",
                columns: new[] { "SagaType", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SagaInstances_Status_UpdatedAtUtc",
                table: "SagaInstances",
                columns: new[] { "Status", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SagaInstances_UpdatedAtUtc",
                table: "SagaInstances",
                column: "UpdatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SagaOutboxMessages_MessageId",
                table: "SagaOutboxMessages",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_SagaOutboxMessages_Status_CreatedAtUtc",
                table: "SagaOutboxMessages",
                columns: new[] { "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SagaTimeouts_Status_DueAtUtc",
                table: "SagaTimeouts",
                columns: new[] { "Status", "DueAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SagaConsumerRegistrations");

            migrationBuilder.DropTable(
                name: "SagaEventLog");

            migrationBuilder.DropTable(
                name: "SagaInstances");

            migrationBuilder.DropTable(
                name: "SagaOutboxMessages");

            migrationBuilder.DropTable(
                name: "SagaTimeouts");
        }
    }
}
