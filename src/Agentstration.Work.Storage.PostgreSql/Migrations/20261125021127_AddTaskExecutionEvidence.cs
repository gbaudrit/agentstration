using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentstration.Work.Storage.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskExecutionEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkTaskAttempts",
                schema: "work",
                columns: table => new
                {
                    WorkspaceId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    OwnerPrincipalId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    TaskId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    PlanId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    AdmittedAtTicks = table.Column<long>(type: "bigint", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkTaskAttempts", x => new { x.WorkspaceId, x.Id });
                    table.ForeignKey(
                        name: "FK_WorkTaskAttempts_WorkPlans_WorkspaceId_PlanId",
                        columns: x => new { x.WorkspaceId, x.PlanId },
                        principalSchema: "work",
                        principalTable: "WorkPlans",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTaskAttempts_WorkspaceId_OwnerPrincipalId_TaskId_Admitt~",
                schema: "work",
                table: "WorkTaskAttempts",
                columns: new[] { "WorkspaceId", "OwnerPrincipalId", "TaskId", "AdmittedAtTicks", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTaskAttempts_WorkspaceId_PlanId",
                schema: "work",
                table: "WorkTaskAttempts",
                columns: new[] { "WorkspaceId", "PlanId" });

            migrationBuilder.Sql("""
                INSERT INTO work."WorkTaskAttempts" ("WorkspaceId", "Id", "OwnerPrincipalId", "TaskId", "PlanId", "AdmittedAtTicks", "Payload")
                SELECT p."WorkspaceId", a->>'id', p."OwnerPrincipalId", a->'taskId'->>'value', p."Id",
                    ((extract(epoch FROM (a->>'admittedAt')::timestamptz) * 10000000)::bigint + 621355968000000000),
                    jsonb_build_object('workspaceId', p."Payload"::jsonb->'workspaceId', 'ownerPrincipalId', p."OwnerPrincipalId",
                        'planId', p."Payload"::jsonb->'id', 'attempt', a)::text
                FROM work."WorkPlans" p
                CROSS JOIN LATERAL jsonb_array_elements(coalesce(p."Payload"::jsonb->'attempts', '[]'::jsonb)) a;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkTaskAttempts",
                schema: "work");
        }
    }
}
