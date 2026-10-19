using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentstration.Runtime.Storage.PostgreSql.Migrations;

/// <inheritdoc />
public partial class InitialPostgreSql : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "runtime");

        migrationBuilder.CreateTable(
            name: "RuntimeExecutionStates",
            schema: "runtime",
            columns: table => new
            {
                WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                RunId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                RuntimeType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                StateId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                ParentStateId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                Payload = table.Column<string>(type: "text", nullable: false),
                CreatedAt = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RuntimeExecutionStates", x => new { x.WorkspaceId, x.RunId, x.RuntimeType, x.StateId });
            });

        migrationBuilder.CreateTable(
            name: "RuntimeRunEvents",
            schema: "runtime",
            columns: table => new
            {
                WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                RunId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Sequence = table.Column<long>(type: "bigint", nullable: false),
                Payload = table.Column<string>(type: "text", nullable: false),
                Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RuntimeRunEvents", x => new { x.WorkspaceId, x.RunId, x.Sequence });
            });

        migrationBuilder.CreateTable(
            name: "RuntimeRuns",
            schema: "runtime",
            columns: table => new
            {
                WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                RunId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                AgentResourceId = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Payload = table.Column<string>(type: "text", nullable: false),
                ETag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                UpdatedAt = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RuntimeRuns", x => new { x.WorkspaceId, x.RunId });
            });

        migrationBuilder.CreateTable(
            name: "RuntimeWorkerAssignments",
            schema: "runtime",
            columns: table => new
            {
                WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                AssignmentId = table.Column<Guid>(type: "uuid", nullable: false),
                TargetKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                TargetRunId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                RuntimeCapability = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                RuntimeCapabilityVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ExecutionMaterialVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                FencingGeneration = table.Column<long>(type: "bigint", nullable: false),
                LeaseExpiresAt = table.Column<long>(type: "bigint", nullable: true),
                ActiveWorkerId = table.Column<Guid>(type: "uuid", nullable: true),
                ActiveWorkerSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                OwnershipTokenDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                Payload = table.Column<string>(type: "text", nullable: false),
                ETag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                UpdatedAt = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RuntimeWorkerAssignments", x => new { x.WorkspaceId, x.AssignmentId });
            });

        migrationBuilder.CreateIndex(
            name: "IX_RuntimeExecutionStates_WorkspaceId_RunId_RuntimeType_Create~",
            schema: "runtime",
            table: "RuntimeExecutionStates",
            columns: new[] { "WorkspaceId", "RunId", "RuntimeType", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_RuntimeRunEvents_WorkspaceId_RunId_Sequence",
            schema: "runtime",
            table: "RuntimeRunEvents",
            columns: new[] { "WorkspaceId", "RunId", "Sequence" });

        migrationBuilder.CreateIndex(
            name: "IX_RuntimeRuns_WorkspaceId_AgentResourceId_CreatedAt",
            schema: "runtime",
            table: "RuntimeRuns",
            columns: new[] { "WorkspaceId", "AgentResourceId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_RuntimeWorkerAssignments_State_ActiveWorkerId_ActiveWorkerSessionId",
            schema: "runtime",
            table: "RuntimeWorkerAssignments",
            columns: new[] { "State", "ActiveWorkerId", "ActiveWorkerSessionId" });

        migrationBuilder.CreateIndex(
            name: "IX_RuntimeWorkerAssignments_State_LeaseExpiresAt",
            schema: "runtime",
            table: "RuntimeWorkerAssignments",
            columns: new[] { "State", "LeaseExpiresAt" });

        migrationBuilder.CreateIndex(
            name: "IX_RuntimeWorkerAssignments_State_RuntimeCapability_CreatedAt",
            schema: "runtime",
            table: "RuntimeWorkerAssignments",
            columns: new[] { "State", "RuntimeCapability", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_RuntimeWorkerAssignments_WorkspaceId_TargetKind_TargetRunId",
            schema: "runtime",
            table: "RuntimeWorkerAssignments",
            columns: new[] { "WorkspaceId", "TargetKind", "TargetRunId" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "RuntimeExecutionStates",
            schema: "runtime");

        migrationBuilder.DropTable(
            name: "RuntimeRunEvents",
            schema: "runtime");

        migrationBuilder.DropTable(
            name: "RuntimeRuns",
            schema: "runtime");

        migrationBuilder.DropTable(
            name: "RuntimeWorkerAssignments",
            schema: "runtime");
    }
}
