using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentstration.Management.Storage.PostgreSql.Migrations;

[DbContext(typeof(ControlPlaneDbContext))]
[Migration("20260907090000_ExplicitResourceScopes")]
public partial class ExplicitResourceScopes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ScopeKey",
            schema: "management",
            table: "ControlPlaneResources",
            type: "character varying(64)",
            maxLength: 64,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "ScopeType",
            schema: "management",
            table: "ControlPlaneResources",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "workspace");

        migrationBuilder.Sql("""
            UPDATE management."ControlPlaneResources"
            SET "ScopeType" = CASE
                    WHEN "WorkspaceId" IS NOT NULL AND "WorkspaceId" <> '00000000-0000-0000-0000-000000000000'::uuid THEN 'workspace'
                    WHEN "TenantId" IS NOT NULL AND "TenantId" <> '00000000-0000-0000-0000-000000000000'::uuid THEN 'tenant'
                    ELSE 'instance'
                END,
                "ScopeKey" = CASE
                    WHEN "WorkspaceId" IS NOT NULL AND "WorkspaceId" <> '00000000-0000-0000-0000-000000000000'::uuid THEN 'workspace:' || lower("WorkspaceId"::text)
                    WHEN "TenantId" IS NOT NULL AND "TenantId" <> '00000000-0000-0000-0000-000000000000'::uuid THEN 'tenant:' || lower("TenantId"::text)
                    ELSE 'instance'
                END
            """);

        migrationBuilder.DropIndex(
            name: "IX_ControlPlaneResources_WorkspaceId_Namespace_Kind_Name",
            schema: "management",
            table: "ControlPlaneResources");

        migrationBuilder.CreateIndex(
            name: "IX_ControlPlaneResources_ScopeKey_Namespace_Kind_Name",
            schema: "management",
            table: "ControlPlaneResources",
            columns: new[] { "ScopeKey", "Namespace", "Kind", "Name" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_ControlPlaneResources_ScopeKey_Namespace_Kind_Name",
            schema: "management",
            table: "ControlPlaneResources");

        migrationBuilder.DropColumn(name: "ScopeKey", schema: "management", table: "ControlPlaneResources");
        migrationBuilder.DropColumn(name: "ScopeType", schema: "management", table: "ControlPlaneResources");

        migrationBuilder.CreateIndex(
            name: "IX_ControlPlaneResources_WorkspaceId_Namespace_Kind_Name",
            schema: "management",
            table: "ControlPlaneResources",
            columns: new[] { "WorkspaceId", "Namespace", "Kind", "Name" },
            unique: true);
    }
}
