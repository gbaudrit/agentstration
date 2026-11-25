using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentstration.Work.Storage.PostgreSql.Migrations
{
    /// <inheritdoc />
    public partial class AddIssueAcknowledgment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OperationId",
                schema: "work",
                table: "WorkSharingPlanMappings",
                type: "character varying(36)",
                maxLength: 36,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OperationId",
                schema: "work",
                table: "WorkSharingPlanMappings");
        }
    }
}
