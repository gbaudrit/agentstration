using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentstration.Work.Storage.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class RetainWorkUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkUsageRecords",
                schema: "work",
                columns: table => new
                {
                    WorkspaceId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    OwnerPrincipalId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    ProjectId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    RootId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    PlanId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    TaskId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    AttemptId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtTicks = table.Column<long>(type: "bigint", nullable: false),
                    Digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkUsageRecords", x => new { x.WorkspaceId, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkUsageRecords_Attempt",
                schema: "work",
                table: "WorkUsageRecords",
                columns: new[] { "WorkspaceId", "OwnerPrincipalId", "ProjectId", "AttemptId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkUsageRecords_History",
                schema: "work",
                table: "WorkUsageRecords",
                columns: new[] { "WorkspaceId", "OwnerPrincipalId", "ProjectId", "CreatedAtTicks", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkUsageRecords_Plan",
                schema: "work",
                table: "WorkUsageRecords",
                columns: new[] { "WorkspaceId", "OwnerPrincipalId", "ProjectId", "PlanId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkUsageRecords_RootRevision",
                schema: "work",
                table: "WorkUsageRecords",
                columns: new[] { "WorkspaceId", "OwnerPrincipalId", "ProjectId", "RootId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkUsageRecords_Task",
                schema: "work",
                table: "WorkUsageRecords",
                columns: new[] { "WorkspaceId", "OwnerPrincipalId", "ProjectId", "TaskId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkUsageRecords",
                schema: "work");
        }
    }
}
