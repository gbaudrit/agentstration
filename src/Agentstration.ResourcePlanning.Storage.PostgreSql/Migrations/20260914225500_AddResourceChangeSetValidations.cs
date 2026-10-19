using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agentstration.ResourcePlanning.Storage.PostgreSql.Migrations;

[DbContext(typeof(ResourcePlanningDbContext))]
[Migration("20260914225500_AddResourceChangeSetValidations")]
public sealed class AddResourceChangeSetValidations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ResourceChangeSetValidations",
            schema: "resource_planning",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                ChangeSetId = table.Column<Guid>(type: "uuid", nullable: false),
                ChangeSetDigest = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                ValidatedAt = table.Column<long>(type: "bigint", nullable: false),
                Payload = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_ResourceChangeSetValidations", value => value.Id));
        migrationBuilder.CreateIndex(
            name: "IX_ResourceChangeSetValidations_TenantId_WorkspaceId_ChangeSetId_ValidatedAt_Id",
            schema: "resource_planning",
            table: "ResourceChangeSetValidations",
            columns: new[] { "TenantId", "WorkspaceId", "ChangeSetId", "ValidatedAt", "Id" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "ResourceChangeSetValidations", schema: "resource_planning");
}
