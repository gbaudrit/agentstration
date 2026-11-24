using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentstration.Work.Storage.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableWorkPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkPlans",
                schema: "work",
                columns: table => new
                {
                    WorkspaceId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    OwnerPrincipalId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkPlans", x => new { x.WorkspaceId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "WorkPlanCommands",
                schema: "work",
                columns: table => new
                {
                    WorkspaceId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    PlanId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    CommandId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    OwnerPrincipalId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkPlanCommands", x => new { x.WorkspaceId, x.PlanId, x.CommandId });
                    table.ForeignKey(
                        name: "FK_WorkPlanCommands_WorkPlans_WorkspaceId_PlanId",
                        columns: x => new { x.WorkspaceId, x.PlanId },
                        principalSchema: "work",
                        principalTable: "WorkPlans",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkPlanRevisions",
                schema: "work",
                columns: table => new
                {
                    WorkspaceId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    PlanId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Number = table.Column<long>(type: "bigint", nullable: false),
                    OwnerPrincipalId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkPlanRevisions", x => new { x.WorkspaceId, x.PlanId, x.Number });
                    table.ForeignKey(
                        name: "FK_WorkPlanRevisions_WorkPlans_WorkspaceId_PlanId",
                        columns: x => new { x.WorkspaceId, x.PlanId },
                        principalSchema: "work",
                        principalTable: "WorkPlans",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkPlans_WorkspaceId_OwnerPrincipalId",
                schema: "work",
                table: "WorkPlans",
                columns: new[] { "WorkspaceId", "OwnerPrincipalId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkPlanCommands",
                schema: "work");

            migrationBuilder.DropTable(
                name: "WorkPlanRevisions",
                schema: "work");

            migrationBuilder.DropTable(
                name: "WorkPlans",
                schema: "work");
        }
    }
}
