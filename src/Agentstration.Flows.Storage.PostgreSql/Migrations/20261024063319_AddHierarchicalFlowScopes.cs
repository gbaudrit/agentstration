using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentstration.Flows.Storage.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddHierarchicalFlowScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FlowResources_WorkspaceId_Namespace_Kind_FlowId_Version",
                schema: "flow",
                table: "FlowResources");

            migrationBuilder.AddColumn<string>(
                name: "ScopeRef",
                schema: "flow",
                table: "FlowResources",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE flow."FlowResources"
                SET "ScopeRef" = '/workspaces/' || lower("WorkspaceId"::text)
                """);

            migrationBuilder.AlterColumn<string>(
                name: "ScopeRef",
                schema: "flow",
                table: "FlowResources",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_FlowResources_ScopeRef_Namespace_Kind_FlowId_Version",
                schema: "flow",
                table: "FlowResources",
                columns: new[] { "ScopeRef", "Namespace", "Kind", "FlowId", "Version" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FlowResources_ScopeRef_Namespace_Kind_FlowId_Version",
                schema: "flow",
                table: "FlowResources");

            migrationBuilder.DropColumn(
                name: "ScopeRef",
                schema: "flow",
                table: "FlowResources");

            migrationBuilder.CreateIndex(
                name: "IX_FlowResources_WorkspaceId_Namespace_Kind_FlowId_Version",
                schema: "flow",
                table: "FlowResources",
                columns: new[] { "WorkspaceId", "Namespace", "Kind", "FlowId", "Version" });
        }
    }
}
