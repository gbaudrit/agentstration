using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Agentstration.Management.Storage.PostgreSql.Migrations;

/// <inheritdoc />
public partial class InitialPostgreSql : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "management");

        migrationBuilder.CreateTable(
            name: "ControlPlaneResources",
            schema: "management",
            columns: table => new
            {
                ResourceId = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                ResourceType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Uid = table.Column<Guid>(type: "uuid", nullable: true),
                Kind = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                Namespace = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                WorkspaceId = table.Column<Guid>(type: "uuid", nullable: true),
                Payload = table.Column<string>(type: "text", nullable: false),
                ETag = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ControlPlaneResources", x => x.ResourceId);
            });

        migrationBuilder.CreateTable(
            name: "RoleAssignments",
            schema: "management",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                PrincipalType = table.Column<int>(type: "integer", nullable: false),
                RoleDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                Scope = table.Column<string>(type: "text", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RoleAssignments", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "RoleDefinitions",
            schema: "management",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                DisplayName = table.Column<string>(type: "text", nullable: false),
                PermissionsJson = table.Column<string>(type: "text", nullable: false),
                IsBuiltIn = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RoleDefinitions", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "SecurityAuditEvents",
            schema: "management",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Outcome = table.Column<int>(type: "integer", nullable: false),
                ActorPrincipalId = table.Column<Guid>(type: "uuid", nullable: true),
                TargetPrincipalId = table.Column<Guid>(type: "uuid", nullable: true),
                TargetAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                WorkspaceId = table.Column<Guid>(type: "uuid", nullable: true),
                ReasonCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                CorrelationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                OccurredAtUtcTicks = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SecurityAuditEvents", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Tenants",
            schema: "management",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                DisplayName = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Tenants", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "TriggerOccurrences",
            schema: "management",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                TriggerUid = table.Column<Guid>(type: "uuid", nullable: false),
                TriggerName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                TriggerNamespace = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                TriggerGeneration = table.Column<long>(type: "bigint", nullable: false),
                Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                ScheduledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                FiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                WorkItemId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                ErrorCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                ErrorMessage = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TriggerOccurrences", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Users",
            schema: "management",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ExternalSubject = table.Column<string>(type: "text", nullable: true),
                Kind = table.Column<int>(type: "integer", nullable: false),
                DisplayName = table.Column<string>(type: "text", nullable: false),
                Email = table.Column<string>(type: "text", nullable: true),
                Status = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Users", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Workspaces",
            schema: "management",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                DisplayName = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Workspaces", x => x.Id);
                table.ForeignKey(
                    name: "FK_Workspaces_Tenants_TenantId",
                    column: x => x.TenantId,
                    principalSchema: "management",
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "ExternalIdentities",
            schema: "management",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Issuer = table.Column<string>(type: "text", nullable: false),
                Subject = table.Column<string>(type: "text", nullable: false),
                PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                LinkedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ExternalIdentities", x => x.Id);
                table.ForeignKey(
                    name: "FK_ExternalIdentities_Users_PrincipalId",
                    column: x => x.PrincipalId,
                    principalSchema: "management",
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "LocalIdentities",
            schema: "management",
            columns: table => new
            {
                AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                LinkedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LocalIdentities", x => x.AccountId);
                table.ForeignKey(
                    name: "FK_LocalIdentities_Users_PrincipalId",
                    column: x => x.PrincipalId,
                    principalSchema: "management",
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PlatformAdministrators",
            schema: "management",
            columns: table => new
            {
                PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                GrantedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PlatformAdministrators", x => x.PrincipalId);
                table.ForeignKey(
                    name: "FK_PlatformAdministrators_Users_PrincipalId",
                    column: x => x.PrincipalId,
                    principalSchema: "management",
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PrincipalPreferences",
            schema: "management",
            columns: table => new
            {
                PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                PreferencesJson = table.Column<string>(type: "text", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PrincipalPreferences", x => x.PrincipalId);
                table.ForeignKey(
                    name: "FK_PrincipalPreferences_Users_PrincipalId",
                    column: x => x.PrincipalId,
                    principalSchema: "management",
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "TenantMemberships",
            schema: "management",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                JoinedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TenantMemberships", x => x.Id);
                table.ForeignKey(
                    name: "FK_TenantMemberships_Tenants_TenantId",
                    column: x => x.TenantId,
                    principalSchema: "management",
                    principalTable: "Tenants",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_TenantMemberships_Users_UserId",
                    column: x => x.UserId,
                    principalSchema: "management",
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "PersonalAccessTokens",
            schema: "management",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                TokenPrefix = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                SecretHash = table.Column<byte[]>(type: "bytea", nullable: false),
                PermissionsJson = table.Column<string>(type: "text", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_PersonalAccessTokens", x => x.Id);
                table.ForeignKey(
                    name: "FK_PersonalAccessTokens_Users_PrincipalId",
                    column: x => x.PrincipalId,
                    principalSchema: "management",
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_PersonalAccessTokens_Workspaces_WorkspaceId",
                    column: x => x.WorkspaceId,
                    principalSchema: "management",
                    principalTable: "Workspaces",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "WorkspaceMemberships",
            schema: "management",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                JoinedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WorkspaceMemberships", x => x.Id);
                table.ForeignKey(
                    name: "FK_WorkspaceMemberships_Users_PrincipalId",
                    column: x => x.PrincipalId,
                    principalSchema: "management",
                    principalTable: "Users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_WorkspaceMemberships_Workspaces_WorkspaceId",
                    column: x => x.WorkspaceId,
                    principalSchema: "management",
                    principalTable: "Workspaces",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ControlPlaneResources_WorkspaceId_Namespace_Kind_Name",
            schema: "management",
            table: "ControlPlaneResources",
            columns: new[] { "WorkspaceId", "Namespace", "Kind", "Name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ExternalIdentities_Issuer_Subject",
            schema: "management",
            table: "ExternalIdentities",
            columns: new[] { "Issuer", "Subject" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ExternalIdentities_PrincipalId",
            schema: "management",
            table: "ExternalIdentities",
            column: "PrincipalId");

        migrationBuilder.CreateIndex(
            name: "IX_LocalIdentities_PrincipalId",
            schema: "management",
            table: "LocalIdentities",
            column: "PrincipalId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PersonalAccessTokens_ExpiresAt",
            schema: "management",
            table: "PersonalAccessTokens",
            column: "ExpiresAt");

        migrationBuilder.CreateIndex(
            name: "IX_PersonalAccessTokens_PrincipalId",
            schema: "management",
            table: "PersonalAccessTokens",
            column: "PrincipalId");

        migrationBuilder.CreateIndex(
            name: "IX_PersonalAccessTokens_TokenPrefix",
            schema: "management",
            table: "PersonalAccessTokens",
            column: "TokenPrefix",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_PersonalAccessTokens_WorkspaceId",
            schema: "management",
            table: "PersonalAccessTokens",
            column: "WorkspaceId");

        migrationBuilder.CreateIndex(
            name: "IX_RoleAssignments_TenantId_PrincipalId",
            schema: "management",
            table: "RoleAssignments",
            columns: new[] { "TenantId", "PrincipalId" });

        migrationBuilder.CreateIndex(
            name: "IX_RoleAssignments_TenantId_PrincipalId_RoleDefinitionId_Scope",
            schema: "management",
            table: "RoleAssignments",
            columns: new[] { "TenantId", "PrincipalId", "RoleDefinitionId", "Scope" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_RoleAssignments_TenantId_Scope",
            schema: "management",
            table: "RoleAssignments",
            columns: new[] { "TenantId", "Scope" });

        migrationBuilder.CreateIndex(
            name: "IX_RoleDefinitions_Name",
            schema: "management",
            table: "RoleDefinitions",
            column: "Name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_SecurityAuditEvents_Action",
            schema: "management",
            table: "SecurityAuditEvents",
            column: "Action");

        migrationBuilder.CreateIndex(
            name: "IX_SecurityAuditEvents_ActorPrincipalId",
            schema: "management",
            table: "SecurityAuditEvents",
            column: "ActorPrincipalId");

        migrationBuilder.CreateIndex(
            name: "IX_SecurityAuditEvents_OccurredAtUtcTicks_Id",
            schema: "management",
            table: "SecurityAuditEvents",
            columns: new[] { "OccurredAtUtcTicks", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_SecurityAuditEvents_TargetPrincipalId",
            schema: "management",
            table: "SecurityAuditEvents",
            column: "TargetPrincipalId");

        migrationBuilder.CreateIndex(
            name: "IX_TenantMemberships_TenantId_UserId",
            schema: "management",
            table: "TenantMemberships",
            columns: new[] { "TenantId", "UserId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_TenantMemberships_UserId",
            schema: "management",
            table: "TenantMemberships",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_Tenants_Name",
            schema: "management",
            table: "Tenants",
            column: "Name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_TriggerOccurrences_WorkspaceId_TriggerUid_ScheduledAt",
            schema: "management",
            table: "TriggerOccurrences",
            columns: new[] { "WorkspaceId", "TriggerUid", "ScheduledAt" });

        migrationBuilder.CreateIndex(
            name: "IX_WorkspaceMemberships_PrincipalId",
            schema: "management",
            table: "WorkspaceMemberships",
            column: "PrincipalId");

        migrationBuilder.CreateIndex(
            name: "IX_WorkspaceMemberships_WorkspaceId_PrincipalId",
            schema: "management",
            table: "WorkspaceMemberships",
            columns: new[] { "WorkspaceId", "PrincipalId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Workspaces_TenantId_Name",
            schema: "management",
            table: "Workspaces",
            columns: new[] { "TenantId", "Name" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ControlPlaneResources",
            schema: "management");

        migrationBuilder.DropTable(
            name: "ExternalIdentities",
            schema: "management");

        migrationBuilder.DropTable(
            name: "LocalIdentities",
            schema: "management");

        migrationBuilder.DropTable(
            name: "PersonalAccessTokens",
            schema: "management");

        migrationBuilder.DropTable(
            name: "PlatformAdministrators",
            schema: "management");

        migrationBuilder.DropTable(
            name: "PrincipalPreferences",
            schema: "management");

        migrationBuilder.DropTable(
            name: "RoleAssignments",
            schema: "management");

        migrationBuilder.DropTable(
            name: "RoleDefinitions",
            schema: "management");

        migrationBuilder.DropTable(
            name: "SecurityAuditEvents",
            schema: "management");

        migrationBuilder.DropTable(
            name: "TenantMemberships",
            schema: "management");

        migrationBuilder.DropTable(
            name: "TriggerOccurrences",
            schema: "management");

        migrationBuilder.DropTable(
            name: "WorkspaceMemberships",
            schema: "management");

        migrationBuilder.DropTable(
            name: "Users",
            schema: "management");

        migrationBuilder.DropTable(
            name: "Workspaces",
            schema: "management");

        migrationBuilder.DropTable(
            name: "Tenants",
            schema: "management");
    }
}
