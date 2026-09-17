using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agentstration.ResourcePlanning.Storage.PostgreSql.Migrations;

[DbContext(typeof(ResourcePlanningDbContext))]
[Migration("20260914223000_InitialResourcePlanning")]
public sealed class InitialResourcePlanning : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "resource_planning");
        migrationBuilder.CreateTable(
            name: "ResourcePlans",
            schema: "resource_planning",
            columns: table => new
            {
                WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                Revision = table.Column<long>(type: "bigint", nullable: false),
                Payload = table.Column<string>(type: "text", nullable: false),
                ETag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                UpdatedAt = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ResourcePlans", value => new { value.WorkspaceId, value.Id }));
        migrationBuilder.CreateTable(
            name: "ResourcePlanActivities",
            schema: "resource_planning",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                PlanRevision = table.Column<long>(type: "bigint", nullable: false),
                CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                Payload = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ResourcePlanActivities", value => value.Id));
        migrationBuilder.CreateIndex(
            name: "IX_ResourcePlans_TenantId_WorkspaceId_Status_UpdatedAt_Id",
            schema: "resource_planning",
            table: "ResourcePlans",
            columns: new[] { "TenantId", "WorkspaceId", "Status", "UpdatedAt", "Id" });
        migrationBuilder.CreateIndex(
            name: "IX_ResourcePlanActivities_TenantId_WorkspaceId_PlanId_CreatedAt_Id",
            schema: "resource_planning",
            table: "ResourcePlanActivities",
            columns: new[] { "TenantId", "WorkspaceId", "PlanId", "CreatedAt", "Id" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ResourcePlanActivities", schema: "resource_planning");
        migrationBuilder.DropTable(name: "ResourcePlans", schema: "resource_planning");
    }
}
