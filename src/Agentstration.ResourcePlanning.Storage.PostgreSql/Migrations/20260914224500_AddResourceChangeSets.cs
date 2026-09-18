using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agentstration.ResourcePlanning.Storage.PostgreSql.Migrations;

[DbContext(typeof(ResourcePlanningDbContext))]
[Migration("20260914224500_AddResourceChangeSets")]
public sealed class AddResourceChangeSets : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ResourceChangeSets",
            schema: "resource_planning",
            columns: table => new
            {
                WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                PlanRevision = table.Column<long>(type: "bigint", nullable: false),
                MaterializationDigest = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                Payload = table.Column<string>(type: "text", nullable: false),
                ETag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ResourceChangeSets", value => new { value.WorkspaceId, value.Id }));
        migrationBuilder.CreateIndex(
            name: "IX_ResourceChangeSets_TenantId_WorkspaceId_CreatedAt_Id",
            schema: "resource_planning",
            table: "ResourceChangeSets",
            columns: new[] { "TenantId", "WorkspaceId", "CreatedAt", "Id" });
        migrationBuilder.CreateIndex(
            name: "IX_ResourceChangeSets_TenantId_WorkspaceId_PlanId_PlanRevision_MaterializationDigest",
            schema: "resource_planning",
            table: "ResourceChangeSets",
            columns: new[] { "TenantId", "WorkspaceId", "PlanId", "PlanRevision", "MaterializationDigest" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "ResourceChangeSets", schema: "resource_planning");
}
