using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentstration.Work.Storage.PostgreSql.Migrations;

/// <inheritdoc />
public partial class InitialPostgreSql : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "work");

        migrationBuilder.CreateTable(
            name: "ConversationMessages",
            schema: "work",
            columns: table => new
            {
                Id = table.Column<string>(type: "text", nullable: false),
                WorkspaceId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                InteractionId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                WorkTaskId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Payload = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ConversationMessages", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Entries",
            schema: "work",
            columns: table => new
            {
                Id = table.Column<string>(type: "character varying(768)", maxLength: 768, nullable: false),
                WorkspaceId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Payload = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Entries", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "EntryDrafts",
            schema: "work",
            columns: table => new
            {
                Id = table.Column<string>(type: "character varying(768)", maxLength: 768, nullable: false),
                WorkspaceId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Payload = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_EntryDrafts", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Interactions",
            schema: "work",
            columns: table => new
            {
                Id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                WorkspaceId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                EntryId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                LastActivityAt = table.Column<long>(type: "bigint", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false),
                Payload = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Interactions", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "PendingActions",
            schema: "work",
            columns: table => new
            {
                Id = table.Column<string>(type: "text", nullable: false),
                WorkspaceId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                InteractionId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                WorkTaskId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ResumeTokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false),
                Payload = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PendingActions", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "WorkItems",
            schema: "work",
            columns: table => new
            {
                Id = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                WorkspaceId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                Type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                RequesterIdentity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                RequestedAgentId = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                SelectedAgentId = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                Title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                Description = table.Column<string>(type: "text", nullable: true),
                InteractionId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                EntryId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                AnchorTaskId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: true),
                FlowRunId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false),
                Payload = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WorkItems", x => new { x.WorkspaceId, x.Id });
            });

        migrationBuilder.CreateTable(
            name: "WorkNotifications",
            schema: "work",
            columns: table => new
            {
                Id = table.Column<string>(type: "text", nullable: false),
                WorkspaceId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<long>(type: "bigint", nullable: false),
                Payload = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WorkNotifications", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "WorkplaceDashboardDrafts",
            schema: "work",
            columns: table => new
            {
                Id = table.Column<string>(type: "character varying(768)", maxLength: 768, nullable: false),
                WorkspaceId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Payload = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WorkplaceDashboardDrafts", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "WorkplaceDashboards",
            schema: "work",
            columns: table => new
            {
                Id = table.Column<string>(type: "character varying(768)", maxLength: 768, nullable: false),
                WorkspaceId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Payload = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WorkplaceDashboards", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "WorkTaskActivities",
            schema: "work",
            columns: table => new
            {
                Id = table.Column<string>(type: "text", nullable: false),
                WorkspaceId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                WorkTaskId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Payload = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WorkTaskActivities", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "WorkTaskArtifacts",
            schema: "work",
            columns: table => new
            {
                Id = table.Column<string>(type: "text", nullable: false),
                WorkspaceId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                WorkTaskId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Payload = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WorkTaskArtifacts", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "WorkTaskResults",
            schema: "work",
            columns: table => new
            {
                Id = table.Column<string>(type: "text", nullable: false),
                WorkspaceId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                WorkTaskId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Payload = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WorkTaskResults", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ConversationMessages_WorkspaceId_InteractionId_CreatedAt",
            schema: "work",
            table: "ConversationMessages",
            columns: new[] { "WorkspaceId", "InteractionId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_Entries_WorkspaceId_Name",
            schema: "work",
            table: "Entries",
            columns: new[] { "WorkspaceId", "Name" });

        migrationBuilder.CreateIndex(
            name: "IX_EntryDrafts_WorkspaceId_Name",
            schema: "work",
            table: "EntryDrafts",
            columns: new[] { "WorkspaceId", "Name" });

        migrationBuilder.CreateIndex(
            name: "IX_Interactions_WorkspaceId_LastActivityAt",
            schema: "work",
            table: "Interactions",
            columns: new[] { "WorkspaceId", "LastActivityAt" });

        migrationBuilder.CreateIndex(
            name: "IX_PendingActions_InteractionId_CreatedAt",
            schema: "work",
            table: "PendingActions",
            columns: new[] { "InteractionId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_PendingActions_ResumeTokenHash",
            schema: "work",
            table: "PendingActions",
            column: "ResumeTokenHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PendingActions_WorkspaceId_Status",
            schema: "work",
            table: "PendingActions",
            columns: new[] { "WorkspaceId", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_WorkItems_FlowRunId",
            schema: "work",
            table: "WorkItems",
            column: "FlowRunId");

        migrationBuilder.CreateIndex(
            name: "IX_WorkItems_RequesterIdentity_CreatedAt",
            schema: "work",
            table: "WorkItems",
            columns: new[] { "RequesterIdentity", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_WorkItems_SelectedAgentId_CreatedAt",
            schema: "work",
            table: "WorkItems",
            columns: new[] { "SelectedAgentId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_WorkItems_Status_CreatedAt",
            schema: "work",
            table: "WorkItems",
            columns: new[] { "Status", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_WorkItems_Type_CreatedAt",
            schema: "work",
            table: "WorkItems",
            columns: new[] { "Type", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_WorkItems_WorkspaceId_AnchorTaskId_UpdatedAt",
            schema: "work",
            table: "WorkItems",
            columns: new[] { "WorkspaceId", "AnchorTaskId", "UpdatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_WorkItems_WorkspaceId_FlowRunId",
            schema: "work",
            table: "WorkItems",
            columns: new[] { "WorkspaceId", "FlowRunId" });

        migrationBuilder.CreateIndex(
            name: "IX_WorkItems_WorkspaceId_InteractionId_UpdatedAt",
            schema: "work",
            table: "WorkItems",
            columns: new[] { "WorkspaceId", "InteractionId", "UpdatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_WorkItems_WorkspaceId_Status_UpdatedAt",
            schema: "work",
            table: "WorkItems",
            columns: new[] { "WorkspaceId", "Status", "UpdatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_WorkNotifications_WorkspaceId_CreatedAt",
            schema: "work",
            table: "WorkNotifications",
            columns: new[] { "WorkspaceId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_WorkNotifications_WorkspaceId_ReadAt",
            schema: "work",
            table: "WorkNotifications",
            columns: new[] { "WorkspaceId", "ReadAt" });

        migrationBuilder.CreateIndex(
            name: "IX_WorkplaceDashboardDrafts_WorkspaceId_Name",
            schema: "work",
            table: "WorkplaceDashboardDrafts",
            columns: new[] { "WorkspaceId", "Name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_WorkplaceDashboards_WorkspaceId_Name",
            schema: "work",
            table: "WorkplaceDashboards",
            columns: new[] { "WorkspaceId", "Name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_WorkTaskActivities_WorkspaceId_WorkTaskId_CreatedAt",
            schema: "work",
            table: "WorkTaskActivities",
            columns: new[] { "WorkspaceId", "WorkTaskId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_WorkTaskArtifacts_WorkspaceId_WorkTaskId_CreatedAt",
            schema: "work",
            table: "WorkTaskArtifacts",
            columns: new[] { "WorkspaceId", "WorkTaskId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_WorkTaskResults_WorkspaceId_WorkTaskId_CreatedAt",
            schema: "work",
            table: "WorkTaskResults",
            columns: new[] { "WorkspaceId", "WorkTaskId", "CreatedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ConversationMessages",
            schema: "work");

        migrationBuilder.DropTable(
            name: "Entries",
            schema: "work");

        migrationBuilder.DropTable(
            name: "EntryDrafts",
            schema: "work");

        migrationBuilder.DropTable(
            name: "Interactions",
            schema: "work");

        migrationBuilder.DropTable(
            name: "PendingActions",
            schema: "work");

        migrationBuilder.DropTable(
            name: "WorkItems",
            schema: "work");

        migrationBuilder.DropTable(
            name: "WorkNotifications",
            schema: "work");

        migrationBuilder.DropTable(
            name: "WorkplaceDashboardDrafts",
            schema: "work");

        migrationBuilder.DropTable(
            name: "WorkplaceDashboards",
            schema: "work");

        migrationBuilder.DropTable(
            name: "WorkTaskActivities",
            schema: "work");

        migrationBuilder.DropTable(
            name: "WorkTaskArtifacts",
            schema: "work");

        migrationBuilder.DropTable(
            name: "WorkTaskResults",
            schema: "work");
    }
}
