using System.Net;
using System.Text.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows.Storage.PostgreSql;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;
using Agentstration.Secrets;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class PostgreSqlStorageProfileTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public void ResourceManagementMigrationsMatchCurrentModel()
    {
        using var context = new Agentstration.ResourceManagement.Storage.PostgreSql.PostgreSqlDesignTimeFactory()
            .CreateDbContext([]);

        Assert.IsFalse(context.Database.HasPendingModelChanges());
    }

    [TestMethod]
    public void FlowMigrationsMatchCurrentModel()
    {
        using var context = new PostgreSqlDesignTimeFactory().CreateDbContext([]);

        Assert.IsFalse(context.Database.HasPendingModelChanges());
    }

    [TestMethod]
    public async Task EmptyDatabaseMigratesAndRemainsReadyAfterRestart()
    {
        var connectionString = Environment.GetEnvironmentVariable("AGENTSTRATION_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
            Assert.Inconclusive("Set AGENTSTRATION_TEST_POSTGRES to run PostgreSQL integration tests.");

        var dataDirectory = Path.Combine(Path.GetTempPath(), $"agentstration-postgresql-profile-{Guid.NewGuid():N}");
        var tenantId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        try
        {
            await StartAndAssertReadyAsync(connectionString, dataDirectory, tenantId, workspaceId, exerciseWorkplace: true);
            await StartAndAssertReadyAsync(connectionString, dataDirectory, tenantId, workspaceId, exerciseWorkplace: false);
            Assert.IsFalse(Directory.EnumerateFiles(dataDirectory, "*.db", SearchOption.AllDirectories).Any(), "The PostgreSQL profile must not create SQLite database files.");
        }
        finally
        {
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, true);
        }
    }

    private static async Task StartAndAssertReadyAsync(string connectionString, string dataDirectory, Guid tenantId, Guid workspaceId, bool exerciseWorkplace)
    {
        await using var host = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Agentstration:Storage:Provider", "PostgreSql");
            builder.UseSetting("ConnectionStrings:Agentstration", connectionString);
            builder.UseSetting("Data:Directory", dataDirectory);
            builder.UseSetting("Agentstration:Bootstrap:InitialBootstrapEnabled", "false");
        });
        using var client = host.CreateClient();
        using var response = await client.GetAsync("/health/ready");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        await AssertRuntimeRunVersionAsync(host.Services, tenantId, workspaceId, exerciseWorkplace);
        await AssertRuntimeWorkerRegistrationAsync(host.Services);
        if (exerciseWorkplace)
        {
            await AssertWorkplaceDefaultReplacementAsync(host.Services);
            await AssertPrincipalPreferencesRoundTripAsync(host.Services);
            await WriteScopedSecretsAsync(host.Services, tenantId, workspaceId);
            await AssertScopeReadAuthorizationAsync(host.Services, tenantId, workspaceId);
        }
        else await AssertScopedSecretsAfterRestartAsync(host.Services, tenantId, workspaceId);
    }

    private static async Task AssertRuntimeRunVersionAsync(IServiceProvider services, Guid tenantId, Guid workspaceId, bool create)
    {
        var store = services.GetRequiredService<IRuntimeRunStore>();
        var scope = RuntimeRunScope.Workspace(tenantId, new(workspaceId), Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"));
        const string runId = "canonical-api-version";
        if (create)
        {
            var created = await store.CreateAsync(new RuntimeRun
            {
                WorkspaceId = scope.WorkspaceId,
                Scope = scope,
                Id = runId,
                Name = runId,
                Properties = new()
                {
                    Agent = new("test-agent", 1),
                    Input = new() { Messages = [new(RuntimeMessageRole.User, "version test")] },
                    Execution = new(),
                    Scope = scope
                },
                Status = new() { CreatedAt = DateTimeOffset.UtcNow }
            }, default);
            Assert.AreEqual("agentstration.io/v1", created.Value.ApiVersion);
            var updated = await store.UpdateAsync(created.Value with
            {
                Status = created.Value.Status with { State = RuntimeRunState.Cancelled }
            }, created.ETag, default);
            Assert.AreEqual("agentstration.io/v1", updated.Value.ApiVersion);
        }

        var restored = await store.GetExactAsync(scope.ScopeRef, runId, default);
        Assert.IsNotNull(restored);
        Assert.AreEqual("agentstration.io/v1", restored.Value.ApiVersion);
        Assert.AreEqual(RuntimeRunState.Cancelled, restored.Value.Status.State);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(restored.Value, JsonOptions));
        Assert.AreEqual("agentstration.io/v1", document.RootElement.GetProperty("apiVersion").GetString());
    }

    private static async Task AssertRuntimeWorkerRegistrationAsync(IServiceProvider services)
    {
        var dispatch = services.GetRequiredService<RuntimeWorkerDispatchService>();
        var workerId = new RuntimeWorkerId(Guid.NewGuid());
        var sessionId = new RuntimeWorkerSessionId(Guid.NewGuid());

        var registration = await dispatch.RegisterAsync(
            workerId,
            sessionId,
            "test-worker",
            1,
            [new("microsoft-agent-framework", "1.0", new HashSet<string>(StringComparer.Ordinal) { "1.0" }, "test")],
            default);

        Assert.AreEqual(workerId, registration.WorkerId);
        Assert.AreEqual(sessionId, registration.WorkerSessionId);
    }

    private static async Task WriteScopedSecretsAsync(IServiceProvider services, Guid tenantId, Guid workspaceId)
    {
        var identities = services.GetRequiredService<IIdentityStore>();
        var now = new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
        await identities.AddTenantAsync(new Tenant(tenantId, $"tenant-{tenantId:N}", "Test tenant", TenantStatus.Active, now), default);
        await identities.AddWorkspaceAsync(new Workspace(workspaceId, tenantId, $"workspace-{workspaceId:N}", "Test workspace", WorkspaceStatus.Active, now), default);

        using var system = services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var store = services.GetRequiredService<IResourceStore>();
        foreach (var scope in SecretScopes(tenantId, workspaceId))
        {
            var vault = new VaultResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = SecretResourceKinds.Vault,
                Metadata = new() { Name = "shared-vault" },
                Definition = new()
                {
                    DisplayName = "Shared vault",
                    ProviderType = "local",
                    UsePolicy = scope == ResourceScopeRef.Instance
                        ? new() { Grants = [new(ResourceScopeRef.Tenant(tenantId), true)] }
                        : new()
                }
            };
            await store.PutExactAsync(scope, vault, null, true, default);
            await store.PutExactAsync(scope, new SecretResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = SecretResourceKinds.Secret,
                Metadata = new() { Name = "shared-secret" },
                Definition = new()
                {
                    DisplayName = "Shared secret",
                    Vault = new ResourceReference("shared-vault", scope),
                    Key = "shared-key",
                    UsePolicy = scope == ResourceScopeRef.Tenant(tenantId)
                        ? new() { Grants = [new(ResourceScopeRef.Workspace(workspaceId))] }
                        : new()
                }
            }, null, true, default);
        }
        await store.PutExactAsync(ResourceScopeRef.Workspace(workspaceId), DiscoveryTarget(workspaceId), null, true, default);
    }

    private static async Task AssertScopeReadAuthorizationAsync(IServiceProvider services, Guid tenantId, Guid workspaceId)
    {
        var identities = services.GetRequiredService<IIdentityStore>();
        var scopes = services.GetRequiredService<IRequestContextScopeFactory>();
        var store = services.GetRequiredService<IResourceStore>();
        var siblingWorkspaceId = Guid.NewGuid();
        var foreignTenantId = Guid.NewGuid();
        var foreignWorkspaceId = Guid.NewGuid();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        await identities.AddWorkspaceAsync(new Workspace(siblingWorkspaceId, tenantId,
            $"sibling-{siblingWorkspaceId:N}", "Sibling workspace", WorkspaceStatus.Active, now), default);
        await identities.AddTenantAsync(new Tenant(foreignTenantId,
            $"foreign-{foreignTenantId:N}", "Foreign tenant", TenantStatus.Active, now), default);
        await identities.AddWorkspaceAsync(new Workspace(foreignWorkspaceId, foreignTenantId,
            $"foreign-{foreignWorkspaceId:N}", "Foreign workspace", WorkspaceStatus.Active, now), default);

        var lateralScopes = new[]
        {
            ResourceScopeRef.Workspace(siblingWorkspaceId),
            ResourceScopeRef.Workspace(foreignWorkspaceId),
            ResourceScopeRef.Tenant(foreignTenantId)
        };
        using (scopes.PushSystem())
        {
            foreach (var scope in lateralScopes)
                await store.PutExactAsync(scope, new VaultResource
                {
                    ApiVersion = ResourceApiVersions.CoreV1,
                    Kind = SecretResourceKinds.Vault,
                    Metadata = new() { Name = "shared-vault" },
                    Definition = new() { DisplayName = "Lateral vault", ProviderType = "local" }
                }, null, true, default);
        }

        using (scopes.Push(new RequestContext(Guid.NewGuid(), tenantId, workspaceId)))
        {
            Assert.IsNotNull(await store.GetExactAsync<VaultResource>(ScopedResourceAddress.Create(
                ResourceScopeRef.Workspace(workspaceId), ResourceNamespace.Default, SecretResourceKinds.Vault, "shared-vault"), default));
            foreach (var denied in lateralScopes)
            {
                await Assert.ThrowsExactlyAsync<ResourceScopeAccessDeniedException>(() =>
                    store.GetExactAsync<VaultResource>(ScopedResourceAddress.Create(
                        denied, ResourceNamespace.Default, SecretResourceKinds.Vault, "shared-vault"), default));
                await Assert.ThrowsExactlyAsync<ResourceScopeAccessDeniedException>(() =>
                    store.ListVisibleAsync<VaultResource>(denied, SecretResourceKinds.Vault, 0, 10, default));
            }
        }
        using (scopes.PushTenant(Guid.NewGuid(), tenantId))
        {
            await Assert.ThrowsExactlyAsync<ResourceScopeAccessDeniedException>(() =>
                store.GetExactAsync<VaultResource>(ScopedResourceAddress.Create(
                    ResourceScopeRef.Tenant(foreignTenantId), ResourceNamespace.Default, SecretResourceKinds.Vault, "shared-vault"), default));
        }
    }

    private static async Task AssertScopedSecretsAfterRestartAsync(IServiceProvider services, Guid tenantId, Guid workspaceId)
    {
        using var system = services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var store = services.GetRequiredService<IResourceStore>();
        foreach (var scope in SecretScopes(tenantId, workspaceId))
        {
            var vault = await store.GetExactAsync<VaultResource>(
                ScopedResourceAddress.Create(scope, ResourceNamespace.Default, SecretResourceKinds.Vault, "shared-vault"), default);
            var secret = await store.GetExactAsync<SecretResource>(
                ScopedResourceAddress.Create(scope, ResourceNamespace.Default, SecretResourceKinds.Secret, "shared-secret"), default);
            Assert.IsNotNull(vault);
            Assert.IsNotNull(secret);
            Assert.AreEqual(scope, vault.Value.ScopeRef);
            Assert.AreEqual(scope, secret.Value.ScopeRef);
            Assert.AreEqual(scope, secret.Value.Definition.Vault.ScopeRef);
            Assert.AreEqual(scope == ResourceScopeRef.Instance ? ResourceScopeRef.Tenant(tenantId) : null,
                vault.Value.Definition.UsePolicy.Grants.SingleOrDefault()?.ScopeRef);
            Assert.AreEqual(scope == ResourceScopeRef.Tenant(tenantId) ? ResourceScopeRef.Workspace(workspaceId) : null,
                secret.Value.Definition.UsePolicy.Grants.SingleOrDefault()?.ScopeRef);
        }
        var target = await store.GetExactAsync<DataSourceAcquisitionTargetResource>(ScopedResourceAddress.Create(
            ResourceScopeRef.Workspace(workspaceId), ResourceNamespace.Default,
            DataSourceResourceKinds.DataSourceAcquisitionTarget, "documents"), default);
        Assert.IsNotNull(target);
        Assert.AreEqual("documents", target.Value.ChoiceId);
        Assert.AreEqual("documents", target.Value.Selector.GetProperty("collection").GetString());
    }

    private static DataSourceAcquisitionTargetResource DiscoveryTarget(Guid workspaceId)
    {
        var scope = ResourceScopeRef.Workspace(workspaceId);
        return new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = DataSourceResourceKinds.DataSourceAcquisitionTarget,
            Metadata = new() { Name = "documents" },
            ScopeRef = scope,
            DisplayName = "Documents",
            DataSourceScopeRef = scope,
            DataSourceUid = Guid.NewGuid(),
            DataSourceName = "origin",
            DataSourceNamespace = ResourceNamespace.Default,
            DataSourceGeneration = 1,
            DiscoveryId = "discovery-1",
            DiscoveryUid = Guid.NewGuid(),
            ChoiceId = "documents",
            ChoiceDisplayName = "Documents",
            Selector = JsonSerializer.SerializeToElement(new { collection = "documents" }),
            Profile = new()
            {
                Name = "profile",
                Namespace = ResourceNamespace.Default,
                ScopeRef = scope,
                Uid = Guid.NewGuid(),
                Generation = 1,
                Version = "1.0.0",
                DefinitionHash = "hash",
                DependencyHash = "dependencies",
                Definition = new() { DisplayName = "Profile", AcquisitionFlow = new() { Name = "acquire" } },
                AcquisitionFlow = new("acquire", ResourceNamespace.Default, "1.0.0", false,
                    null, null, DataSourceFlowContracts.Acquisition, "sha256:acquire")
                { ScopeRef = scope }
            },
            PromotedAt = DateTimeOffset.UtcNow,
            PromotedBy = Guid.Empty
        };
    }

    private static ResourceScopeRef[] SecretScopes(Guid tenantId, Guid workspaceId) =>
        [ResourceScopeRef.Instance, ResourceScopeRef.Tenant(tenantId), ResourceScopeRef.Workspace(workspaceId)];

    private static async Task AssertWorkplaceDefaultReplacementAsync(IServiceProvider services)
    {
        var repository = services.GetRequiredService<IWorkplaceRepository>();
        var workspaceId = new WorkspaceId(Guid.NewGuid());
        var publishedAt = DateTimeOffset.UtcNow;
        var home = new WorkplaceDashboard
        {
            Id = new("home"),
            WorkspaceId = workspaceId,
            Name = "home",
            DisplayName = "Home",
            IsDefault = true,
            PublishedAt = publishedAt
        };
        var replacement = home with
        {
            Id = new("operations"),
            Name = "operations",
            DisplayName = "Operations",
            PublishedAt = publishedAt.AddSeconds(1)
        };

        await repository.ReplaceDefaultDashboardAsync(home, default);
        await repository.ReplaceDefaultDashboardAsync(replacement, default);

        var dashboards = await repository.ListDashboardsAsync(workspaceId, default);
        Assert.HasCount(2, dashboards);
        Assert.AreEqual(replacement.Id, dashboards.Single(value => value.IsDefault).Id);
    }

    private static async Task AssertPrincipalPreferencesRoundTripAsync(IServiceProvider services)
    {
        var store = services.GetRequiredService<IIdentityStore>();
        var now = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var principal = new Principal(Guid.NewGuid(), PrincipalKind.Human, "PostgreSQL preferences", null, PrincipalStatus.Active, now);
        var expected = new PrincipalPreferences(
            principal.Id,
            ThemePreference.Dark,
            now,
            "fr-FR",
            Guid.NewGuid(),
            Guid.NewGuid());

        await store.AddPrincipalAsync(principal, default);
        await store.UpsertPrincipalPreferencesAsync(expected, default);

        Assert.AreEqual(expected, await store.GetPrincipalPreferencesAsync(principal.Id, default));
    }
}
