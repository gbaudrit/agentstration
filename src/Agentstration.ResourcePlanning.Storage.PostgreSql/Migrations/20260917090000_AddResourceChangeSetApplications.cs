using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agentstration.ResourcePlanning.Storage.PostgreSql.Migrations;

[DbContext(typeof(ResourcePlanningDbContext))]
[Migration("20260917090000_AddResourceChangeSetApplications")]
public sealed class AddResourceChangeSetApplications : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ResourceChangeSetApplications",
            schema: "resource_planning",
            columns: table => new
            {
                WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                ChangeSetId = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                Payload = table.Column<string>(type: "text", nullable: false),
                ETag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ResourceChangeSetApplications", value => new { value.WorkspaceId, value.ChangeSetId }));
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "ResourceChangeSetApplications", schema: "resource_planning");
}
