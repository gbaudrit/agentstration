using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentstration.Runtime.Storage.PostgreSql.Migrations;

[DbContext(typeof(RuntimeRunDbContext))]
[Migration("20261003101500_AddRuntimeWorkerAssignments")]
public sealed class AddRuntimeWorkerAssignments : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
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
                OwnershipTokenDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                Payload = table.Column<string>(type: "text", nullable: false),
                ETag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                UpdatedAt = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_RuntimeWorkerAssignments", value => new { value.WorkspaceId, value.AssignmentId }));

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

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "RuntimeWorkerAssignments", schema: "runtime");
}
