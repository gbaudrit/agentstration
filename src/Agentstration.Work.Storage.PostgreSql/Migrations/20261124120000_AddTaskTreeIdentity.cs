using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agentstration.Work.Storage.PostgreSql.Migrations;

[DbContext(typeof(WorkDbContext))]
[Migration("20261124120000_AddTaskTreeIdentity")]
public sealed class AddTaskTreeIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("TaskTreeId", "WorkItems", schema: "work",
            type: "character varying(36)", maxLength: 36, nullable: true);
        migrationBuilder.CreateIndex("IX_WorkItems_WorkspaceId_OwnerPrincipalId_TaskTreeId", "WorkItems",
            new[] { "WorkspaceId", "OwnerPrincipalId", "TaskTreeId" }, schema: "work");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_WorkItems_WorkspaceId_OwnerPrincipalId_TaskTreeId", "WorkItems", "work");
        migrationBuilder.DropColumn("TaskTreeId", "WorkItems", "work");
    }
}
