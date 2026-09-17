using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agentstration.ResourcePlanning.Storage.PostgreSql.Migrations;

[DbContext(typeof(ResourcePlanningDbContext))]
[Migration("20260917080000_AddResourcePlanBindingDrafts")]
public sealed class AddResourcePlanBindingDrafts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ResourcePlanBindingDrafts",
            schema: "resource_planning",
            columns: table => new
            {
                WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                Payload = table.Column<string>(type: "text", nullable: false),
                ETag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ResourcePlanBindingDrafts", value => new { value.WorkspaceId, value.PlanId }));
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "ResourcePlanBindingDrafts", schema: "resource_planning");
}
