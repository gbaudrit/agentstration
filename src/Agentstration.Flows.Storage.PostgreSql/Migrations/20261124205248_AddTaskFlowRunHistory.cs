using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentstration.Flows.Storage.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskFlowRunHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RunCreatedAtTicks",
                schema: "flow",
                table: "FlowResources",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "WorkTaskId",
                schema: "flow",
                table: "FlowResources",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE flow."FlowResources"
                SET "WorkTaskId" = "Payload"::jsonb ->> 'workTaskId',
                    "RunCreatedAtTicks" = EXTRACT(EPOCH FROM regexp_replace("Payload"::jsonb ->> 'createdAt', '\.[0-9]+', '')::timestamptz)::bigint * 10000000
                        + COALESCE(NULLIF(rpad(substring("Payload"::jsonb ->> 'createdAt' from '\.([0-9]+)'), 7, '0'), ''), '0')::bigint
                        + 621355968000000000
                WHERE "Kind" = 'run';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_FlowResources_TaskHistory",
                schema: "flow",
                table: "FlowResources",
                columns: new[] { "ScopeRef", "Kind", "TenantId", "PrincipalId", "WorkTaskId", "RunCreatedAtTicks", "Key" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_FlowResources_TaskHistory",
                schema: "flow",
                table: "FlowResources");

            migrationBuilder.DropColumn(
                name: "RunCreatedAtTicks",
                schema: "flow",
                table: "FlowResources");

            migrationBuilder.DropColumn(
                name: "WorkTaskId",
                schema: "flow",
                table: "FlowResources");
        }
    }
}
