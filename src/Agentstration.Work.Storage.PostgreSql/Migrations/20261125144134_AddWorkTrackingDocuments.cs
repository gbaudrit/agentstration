using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentstration.Work.Storage.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkTrackingDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkTrackingDocuments",
                schema: "work",
                columns: table => new
                {
                    WorkspaceId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    OwnerPrincipalId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    ProjectId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkTrackingDocuments", x => new { x.WorkspaceId, x.OwnerPrincipalId, x.ProjectId, x.Key });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkTrackingDocuments",
                schema: "work");
        }
    }
}
