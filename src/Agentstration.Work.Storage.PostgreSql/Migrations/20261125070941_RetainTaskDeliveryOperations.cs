using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentstration.Work.Storage.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class RetainTaskDeliveryOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkTaskDeliveries",
                schema: "work",
                columns: table => new
                {
                    WorkspaceId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    OwnerPrincipalId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    ProjectId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    PlanId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    TaskId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    AttemptId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    IntentDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtTicks = table.Column<long>(type: "bigint", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkTaskDeliveries", x => new { x.WorkspaceId, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTaskDeliveries_Attempt",
                schema: "work",
                table: "WorkTaskDeliveries",
                columns: new[] { "WorkspaceId", "OwnerPrincipalId", "ProjectId", "AttemptId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkTaskDeliveries_History",
                schema: "work",
                table: "WorkTaskDeliveries",
                columns: new[] { "WorkspaceId", "OwnerPrincipalId", "ProjectId", "CreatedAtTicks", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkTaskDeliveries",
                schema: "work");
        }
    }
}
