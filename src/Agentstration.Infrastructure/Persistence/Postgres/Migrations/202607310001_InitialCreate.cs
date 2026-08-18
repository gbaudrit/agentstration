using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentstration.Infrastructure.Persistence.Postgres.Migrations;

public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "agent_platform");
        migrationBuilder.CreateTable(name: "workspaces", schema: "agent_platform", columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            Name = table.Column<string>(type: "text", nullable: false),
            CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_workspaces", x => x.Id));
        migrationBuilder.CreateTable(name: "inboxes", schema: "agent_platform", columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
            Name = table.Column<string>(type: "text", nullable: false),
            Slug = table.Column<string>(type: "text", nullable: false),
            ApiKeyHash = table.Column<string>(type: "text", nullable: false),
            CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_inboxes", x => x.Id));
        migrationBuilder.CreateTable(name: "items", schema: "agent_platform", columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
            InboxId = table.Column<Guid>(type: "uuid", nullable: false),
            Status = table.Column<string>(type: "text", nullable: false),
            ContentHash = table.Column<string>(type: "text", nullable: false),
            ExternalId = table.Column<string>(type: "text", nullable: true),
            RawContent = table.Column<string>(type: "text", nullable: false),
            NormalizedContent = table.Column<string>(type: "text", nullable: true),
            CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_items", x => x.Id));
        migrationBuilder.CreateTable(name: "memory_entries", schema: "agent_platform", columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
            ItemId = table.Column<Guid>(type: "uuid", nullable: true),
            MissionId = table.Column<Guid>(type: "uuid", nullable: true),
            Kind = table.Column<string>(type: "text", nullable: false),
            Content = table.Column<string>(type: "text", nullable: false),
            CategoriesJson = table.Column<string>(type: "jsonb", nullable: false),
            CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_memory_entries", x => x.Id));
        migrationBuilder.CreateTable(name: "missions", schema: "agent_platform", columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
            Name = table.Column<string>(type: "text", nullable: false),
            Objective = table.Column<string>(type: "text", nullable: false),
            SourceUrl = table.Column<string>(type: "text", nullable: false),
            FrequencySeconds = table.Column<int>(type: "integer", nullable: false),
            Threshold = table.Column<decimal>(type: "numeric", nullable: true),
            Status = table.Column<string>(type: "text", nullable: false),
            NextRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_missions", x => x.Id));
        migrationBuilder.CreateTable(name: "mission_runs", schema: "agent_platform", columns: table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
            MissionId = table.Column<Guid>(type: "uuid", nullable: false),
            Status = table.Column<string>(type: "text", nullable: false),
            Observation = table.Column<decimal>(type: "numeric", nullable: true),
            Changed = table.Column<bool>(type: "boolean", nullable: false),
            Error = table.Column<string>(type: "text", nullable: true),
            StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
        }, constraints: table => table.PrimaryKey("PK_mission_runs", x => x.Id));
        migrationBuilder.CreateIndex(name: "IX_inboxes_WorkspaceId_Slug", schema: "agent_platform", table: "inboxes", columns: new[] { "WorkspaceId", "Slug" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_items_WorkspaceId_InboxId_ContentHash", schema: "agent_platform", table: "items", columns: new[] { "WorkspaceId", "InboxId", "ContentHash" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_items_WorkspaceId_Status_CreatedAt", schema: "agent_platform", table: "items", columns: new[] { "WorkspaceId", "Status", "CreatedAt" });
        migrationBuilder.CreateIndex(name: "IX_memory_entries_WorkspaceId_ItemId_CreatedAt", schema: "agent_platform", table: "memory_entries", columns: new[] { "WorkspaceId", "ItemId", "CreatedAt" });
        migrationBuilder.CreateIndex(name: "IX_missions_WorkspaceId_Status_NextRunAt", schema: "agent_platform", table: "missions", columns: new[] { "WorkspaceId", "Status", "NextRunAt" });
        migrationBuilder.CreateIndex(name: "IX_mission_runs_WorkspaceId_MissionId_StartedAt", schema: "agent_platform", table: "mission_runs", columns: new[] { "WorkspaceId", "MissionId", "StartedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "inboxes", schema: "agent_platform");
        migrationBuilder.DropTable(name: "items", schema: "agent_platform");
        migrationBuilder.DropTable(name: "memory_entries", schema: "agent_platform");
        migrationBuilder.DropTable(name: "mission_runs", schema: "agent_platform");
        migrationBuilder.DropTable(name: "missions", schema: "agent_platform");
        migrationBuilder.DropTable(name: "workspaces", schema: "agent_platform");
    }
}
