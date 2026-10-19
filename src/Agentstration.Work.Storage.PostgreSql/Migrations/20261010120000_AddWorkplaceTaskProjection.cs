using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentstration.Work.Storage.PostgreSql.Migrations;

[DbContext(typeof(WorkDbContext))]
[Migration("20261010120000_AddWorkplaceTaskProjection")]
public partial class AddWorkplaceTaskProjection : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "IsWorkplaceTask",
            schema: "work",
            table: "WorkItems",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.Sql("""
            UPDATE work."WorkItems"
            SET "IsWorkplaceTask" = TRUE
            WHERE ("EntryId" IS NOT NULL AND "InteractionId" ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$')
               OR ("Payload"::jsonb -> 'metadata' ->> 'origin') = 'trigger';
            """);

        migrationBuilder.CreateIndex(
            name: "IX_WorkItems_WorkspaceId_OwnerPrincipalId_IsWorkplaceTask_UpdatedAt",
            schema: "work",
            table: "WorkItems",
            columns: new[] { "WorkspaceId", "OwnerPrincipalId", "IsWorkplaceTask", "UpdatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_WorkItems_WorkspaceId_OwnerPrincipalId_IsWorkplaceTask_UpdatedAt",
            schema: "work",
            table: "WorkItems");

        migrationBuilder.DropColumn(
            name: "IsWorkplaceTask",
            schema: "work",
            table: "WorkItems");
    }
}
