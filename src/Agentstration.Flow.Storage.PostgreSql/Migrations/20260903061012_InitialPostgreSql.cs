using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentstration.Flow.Storage.PostgreSql.Migrations;

/// <inheritdoc />
public partial class InitialPostgreSql : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "flow");

        migrationBuilder.CreateTable(
            name: "FlowResources",
            schema: "flow",
            columns: table => new
            {
                Key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                Kind = table.Column<string>(type: "text", nullable: false),
                FlowId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Namespace = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Version = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: true),
                Payload = table.Column<string>(type: "text", nullable: false),
                ETag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                UpdatedAt = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FlowResources", x => x.Key);
            });

        migrationBuilder.CreateIndex(
            name: "IX_FlowResources_WorkspaceId_Kind_TenantId_PrincipalId_Namesp~1",
            schema: "flow",
            table: "FlowResources",
            columns: new[] { "WorkspaceId", "Kind", "TenantId", "PrincipalId", "Namespace", "FlowId", "Status", "UpdatedAt", "Key" });

        migrationBuilder.CreateIndex(
            name: "IX_FlowResources_WorkspaceId_Kind_TenantId_PrincipalId_Namespa~",
            schema: "flow",
            table: "FlowResources",
            columns: new[] { "WorkspaceId", "Kind", "TenantId", "PrincipalId", "Namespace", "FlowId", "UpdatedAt", "Key" });

        migrationBuilder.CreateIndex(
            name: "IX_FlowResources_WorkspaceId_Kind_TenantId_PrincipalId_Status_~",
            schema: "flow",
            table: "FlowResources",
            columns: new[] { "WorkspaceId", "Kind", "TenantId", "PrincipalId", "Status", "UpdatedAt", "Key" });

        migrationBuilder.CreateIndex(
            name: "IX_FlowResources_WorkspaceId_Kind_TenantId_PrincipalId_Updated~",
            schema: "flow",
            table: "FlowResources",
            columns: new[] { "WorkspaceId", "Kind", "TenantId", "PrincipalId", "UpdatedAt", "Key" });

        migrationBuilder.CreateIndex(
            name: "IX_FlowResources_WorkspaceId_Namespace_Kind_FlowId_Version",
            schema: "flow",
            table: "FlowResources",
            columns: new[] { "WorkspaceId", "Namespace", "Kind", "FlowId", "Version" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "FlowResources",
            schema: "flow");
    }
}
