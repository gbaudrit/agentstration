using System.Net;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class PostgreSqlStorageProfileTests
{
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
        if (exerciseWorkplace)
        {
            await AssertWorkplaceDefaultReplacementAsync(host.Services);
            await AssertPrincipalPreferencesRoundTripAsync(host.Services);
            await WriteScopedSecretsAsync(host.Services, tenantId, workspaceId);
        }
        else await AssertScopedSecretsAfterRestartAsync(host.Services, tenantId, workspaceId);
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
