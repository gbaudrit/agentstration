using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentstration.Work.Storage.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskProviderOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkTaskOperations",
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
                    table.PrimaryKey("PK_WorkTaskOperations", x => new { x.WorkspaceId, x.Id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTaskOperations_ProjectHistory",
                schema: "work",
                table: "WorkTaskOperations",
                columns: new[] { "WorkspaceId", "OwnerPrincipalId", "ProjectId", "CreatedAtTicks", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkTaskOperations",
                schema: "work");
        }
    }
}
