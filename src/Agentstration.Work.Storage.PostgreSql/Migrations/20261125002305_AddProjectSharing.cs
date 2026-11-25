using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentstration.Work.Storage.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkSharingOperations",
                schema: "work",
                columns: table => new
                {
                    WorkspaceId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    OwnerPrincipalId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    ProjectId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtTicks = table.Column<long>(type: "bigint", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkSharingOperations", x => new { x.WorkspaceId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "WorkSharingPlanMappings",
                schema: "work",
                columns: table => new
                {
                    WorkspaceId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    OwnerPrincipalId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    PlanId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    ProjectId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkSharingPlanMappings", x => new { x.WorkspaceId, x.OwnerPrincipalId, x.PlanId });
                });

            migrationBuilder.CreateTable(
                name: "WorkSharingStates",
                schema: "work",
                columns: table => new
                {
                    WorkspaceId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    ProjectId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    OwnerPrincipalId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkSharingStates", x => new { x.WorkspaceId, x.ProjectId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkSharingOperations_ProjectHistory",
                schema: "work",
                table: "WorkSharingOperations",
                columns: new[] { "WorkspaceId", "OwnerPrincipalId", "ProjectId", "CreatedAtTicks", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkSharingOperations",
                schema: "work");

            migrationBuilder.DropTable(
                name: "WorkSharingPlanMappings",
                schema: "work");

            migrationBuilder.DropTable(
                name: "WorkSharingStates",
                schema: "work");
        }
    }
}
