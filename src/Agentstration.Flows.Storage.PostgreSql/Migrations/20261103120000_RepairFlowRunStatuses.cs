using Microsoft.EntityFrameworkCore.Migrations;

namespace Agentstration.Flows.Storage.PostgreSql.Migrations;

/// <inheritdoc />
public partial class RepairFlowRunStatuses : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Earlier updates changed the payload without maintaining the queryable status.
        migrationBuilder.Sql(
            """
            UPDATE flow."FlowResources"
            SET "Status" = ("Payload"::jsonb ->> 'status')::integer
            WHERE "Kind" = 'run'
              AND "Status" IS DISTINCT FROM ("Payload"::jsonb ->> 'status')::integer
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Repaired metadata remains consistent with the authoritative payload on downgrade.
    }
}
