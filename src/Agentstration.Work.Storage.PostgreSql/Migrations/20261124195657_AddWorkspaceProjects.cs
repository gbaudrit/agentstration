using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentstration.Work.Storage.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceProjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkProjects",
                schema: "work",
                columns: table => new
                {
                    WorkspaceId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    OwnerPrincipalId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Namespace = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkProjects", x => new { x.WorkspaceId, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkProjects_WorkspaceId_OwnerPrincipalId_Namespace_Name",
                schema: "work",
                table: "WorkProjects",
                columns: new[] { "WorkspaceId", "OwnerPrincipalId", "Namespace", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkProjects",
                schema: "work");
        }
    }
}
