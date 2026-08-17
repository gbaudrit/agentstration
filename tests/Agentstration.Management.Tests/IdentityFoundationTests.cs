using System.Net;
using System.Net.Http.Json;
using Agentstration.Infrastructure;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class IdentityFoundationTests
{
    [TestMethod]
    public async Task EmptyDatabaseBootstrapsOnceAndUsesStableIds()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        var first = fixture.Context.Current;
        var store = fixture.Services.GetRequiredService<IIdentityStore>();

        await fixture.Bootstrap.EnsureInitializedAsync(default);

        Assert.AreEqual(first, fixture.Context.Current);
        Assert.AreEqual(1, (await store.ListTenantsAsync(default)).Count);
        Assert.AreEqual(1, (await store.ListWorkspacesAsync(first.TenantId, default)).Count);
        Assert.IsNotNull(await store.FindMembershipAsync(first.TenantId, first.UserId, default));
        Assert.IsNotNull(await store.FindWorkspaceMembershipAsync(first.WorkspaceId, first.PrincipalId, default));
        Assert.AreEqual(1, (await store.ListRoleAssignmentsAsync(first.TenantId, first.UserId, default)).Count);
    }

    [TestMethod]
    public async Task BootstrapRepairsMissingWorkspaceMembershipAndOwnerAssignment()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        var original = fixture.Context.Current;
        await fixture.ExecuteSqlAsync("DELETE FROM RoleAssignments; DELETE FROM TenantMemberships; DELETE FROM Workspaces;");

        await fixture.Bootstrap.EnsureInitializedAsync(default);

        var repaired = fixture.Context.Current;
        var store = fixture.Services.GetRequiredService<IIdentityStore>();
        Assert.AreEqual(original.TenantId, repaired.TenantId);
        Assert.AreEqual(original.UserId, repaired.UserId);
        Assert.AreNotEqual(original.WorkspaceId, repaired.WorkspaceId);
        Assert.IsNotNull(await store.FindMembershipAsync(repaired.TenantId, repaired.UserId, default));
        Assert.AreEqual(1, (await store.ListRoleAssignmentsAsync(repaired.TenantId, repaired.UserId, default)).Count);
    }

    [TestMethod]
    public async Task DuplicateTenantMembershipIsRejected()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        var context = fixture.Context.Current;
        var store = fixture.Services.GetRequiredService<IIdentityStore>();
        var duplicate = new TenantMembership(Guid.NewGuid(), context.TenantId, context.UserId, MembershipStatus.Active, DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<ControlPlaneConcurrencyException>(() => store.AddMembershipAsync(duplicate, default));
    }

    [TestMethod]
    public async Task TenantOwnerInheritsWorkspaceAccessButReaderCannotWriteAndUnassignedUserCannotRead()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        var context = fixture.Context.Current;
        var store = fixture.Services.GetRequiredService<IIdentityStore>();
        var authorization = fixture.Services.GetRequiredService<IAuthorizationService>();
        Assert.IsTrue(await authorization.HasPermissionAsync(context, AuthorizationPermissions.ResourcesWrite, default));

        var now = DateTimeOffset.UtcNow;
        var reader = new RoleDefinition(Guid.NewGuid(), "Reader-test", "Reader", [AuthorizationPermissions.ResourcesRead], false);
        await store.AddRoleDefinitionAsync(reader, default);
        var readerPrincipal = new Principal(Guid.NewGuid(), PrincipalKind.Human, "Reader", null, PrincipalStatus.Active, now);
        await store.AddPrincipalAsync(readerPrincipal, default);
        await store.AddMembershipAsync(new TenantMembership(Guid.NewGuid(), context.TenantId, readerPrincipal.Id, MembershipStatus.Active, now), default);
        await store.AddWorkspaceMembershipAsync(new WorkspaceMembership(Guid.NewGuid(), context.WorkspaceId, readerPrincipal.Id, MembershipStatus.Active, now), default);
        await store.AddRoleAssignmentAsync(new RoleAssignment(Guid.NewGuid(), context.TenantId, readerPrincipal.Id, PrincipalType.User, reader.Id, AuthorizationScopes.Workspace(context.WorkspaceId)), default);
        var readerContext = context with { PrincipalId = readerPrincipal.Id };
        Assert.IsTrue(await authorization.HasPermissionAsync(readerContext, AuthorizationPermissions.ResourcesRead, default));
        Assert.IsFalse(await authorization.HasPermissionAsync(readerContext, AuthorizationPermissions.ResourcesWrite, default));

        var unassigned = new Principal(Guid.NewGuid(), PrincipalKind.Human, "Unassigned", null, PrincipalStatus.Active, now);
        await store.AddPrincipalAsync(unassigned, default);
        await store.AddMembershipAsync(new TenantMembership(Guid.NewGuid(), context.TenantId, unassigned.Id, MembershipStatus.Active, now), default);
        await store.AddWorkspaceMembershipAsync(new WorkspaceMembership(Guid.NewGuid(), context.WorkspaceId, unassigned.Id, MembershipStatus.Active, now), default);
        Assert.IsFalse(await authorization.HasPermissionAsync(context with { PrincipalId = unassigned.Id }, AuthorizationPermissions.ResourcesRead, default));
    }

    [TestMethod]
    public async Task ResourceLookupIsExplicitlyWorkspaceAndTenantScoped()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        var identity = fixture.Services.GetRequiredService<IIdentityStore>();
        var resources = fixture.Services.GetRequiredService<IControlPlaneStore>();
        var first = fixture.Context.Current;
        var now = DateTimeOffset.UtcNow;
        var secondWorkspace = new Workspace(Guid.NewGuid(), first.TenantId, "finance", "Finance", WorkspaceStatus.Active, now);
        await identity.AddWorkspaceAsync(secondWorkspace, default);

        var key = new ResourceKey(ResourceKinds.RuntimeProfile, "shared");
        await resources.PutAsync(Profile("shared", first), null, true, default);
        fixture.Context.Initialize(first with { WorkspaceId = secondWorkspace.Id });
        var second = fixture.Context.Current;
        await resources.PutAsync(Profile("shared", second), null, true, default);

        Assert.IsNotNull(await resources.GetAsync<RuntimeProfileResource>(key, default));

        var otherTenant = new Tenant(Guid.NewGuid(), "other", "Other", TenantStatus.Active, now);
        await identity.AddTenantAsync(otherTenant, default);
        var otherWorkspace = new Workspace(Guid.NewGuid(), otherTenant.Id, "default", "Default", WorkspaceStatus.Active, now);
        await identity.AddWorkspaceAsync(otherWorkspace, default);
        fixture.Context.Initialize(second with { TenantId = otherTenant.Id, WorkspaceId = otherWorkspace.Id });
        Assert.IsNull(await resources.GetAsync<RuntimeProfileResource>(key, default));
    }

    [TestMethod]
    public void RequestContextScopeIsAmbientAndRestoresTheStandaloneFallback()
    {
        var accessor = new CurrentRequestContext();
        var fallback = new RequestContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var selected = fallback with { WorkspaceId = Guid.NewGuid() };
        accessor.Initialize(fallback);
        using (accessor.Push(selected)) Assert.AreEqual(selected, accessor.Current);
        Assert.AreEqual(fallback, accessor.Current);
    }

    [TestMethod]
    public void ExplicitSystemScopeIsGlobalAndRestoresTheWorkspaceFallback()
    {
        var accessor = new CurrentRequestContext();
        var fallback = new RequestContext(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        accessor.Initialize(fallback);

        using (accessor.PushSystem())
        {
            Assert.AreEqual(ControlPlaneAccessMode.System, accessor.AccessMode);
            Assert.IsFalse(accessor.IsInitialized);
            Assert.ThrowsExactly<InvalidOperationException>(() => _ = accessor.Current);
        }

        Assert.AreEqual(ControlPlaneAccessMode.Workspace, accessor.AccessMode);
        Assert.AreEqual(fallback, accessor.Current);
    }

    [TestMethod]
    public async Task WorkspaceCreationDoesNotRequireAResourceGroup()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        var administration = fixture.Services.GetRequiredService<IdentityAdministrationService>();
        var store = fixture.Services.GetRequiredService<IIdentityStore>();

        var workspace = await administration.CreateWorkspaceAsync("support", "Customer support", default);

        Assert.AreEqual(fixture.Context.Current.TenantId, workspace.TenantId);
        Assert.AreEqual("support", workspace.Name);
    }

    [TestMethod]
    public async Task ContextApiSelectsAnAuthorizedWorkspaceAndRejectsAnUnknownOne()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var created = await client.PostAsJsonAsync("/api/identity/workspaces", new { name = "finance", displayName = "Finance" });
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var workspace = await created.Content.ReadFromJsonAsync<Workspace>();
        Assert.IsNotNull(workspace);

        var selected = await client.PostAsJsonAsync("/api/identity/context/workspace", new { workspaceId = workspace.Id });
        Assert.AreEqual(HttpStatusCode.OK, selected.StatusCode);
        var context = await client.GetFromJsonAsync<ConsoleContextView>("/api/identity/context");
        Assert.AreEqual(workspace.Id, context?.Context.WorkspaceId);

        var denied = await client.PostAsJsonAsync("/api/identity/context/workspace", new { workspaceId = Guid.NewGuid() });
        Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [TestMethod]
    public async Task ExternalIdentityUsesIssuerAndSubjectAndIgnoresEmailChanges()
    {
        await using var fixture = await IdentityFixture.CreateAsync();
        var store = fixture.Services.GetRequiredService<IIdentityStore>();
        var resolver = fixture.Services.GetRequiredService<IPrincipalResolver>();
        var now = DateTimeOffset.UtcNow;
        var first = new Principal(Guid.NewGuid(), PrincipalKind.Human, "First", "before@example.test", PrincipalStatus.Active, now);
        var second = new Principal(Guid.NewGuid(), PrincipalKind.Human, "Second", null, PrincipalStatus.Active, now);
        await store.AddPrincipalAsync(first, default);
        await store.AddPrincipalAsync(second, default);
        await store.AddExternalIdentityAsync(new ExternalIdentity(Guid.NewGuid(), "https://issuer-a.test", "same-subject", first.Id, now), default);
        await store.AddExternalIdentityAsync(new ExternalIdentity(Guid.NewGuid(), "https://issuer-b.test", "same-subject", second.Id, now), default);

        Assert.AreEqual(first.Id, (await resolver.ResolveAsync("https://issuer-a.test", "same-subject", default))?.Id);
        Assert.AreEqual(second.Id, (await resolver.ResolveAsync("https://issuer-b.test", "same-subject", default))?.Id);

        await store.UpdatePrincipalAsync(first with { Email = "after@example.test" }, default);
        Assert.AreEqual(first.Id, (await resolver.ResolveAsync("https://issuer-a.test", "same-subject", default))?.Id);
    }

    private static RuntimeProfileResource Profile(string id, RequestContext context) => new()
    {
        Metadata = new ResourceMetadata { Name = id },
        Kind = ResourceKinds.RuntimeProfile,
        ApiVersion = ManagementApiVersions.CoreV1,
        TenantId = context.TenantId,
        WorkspaceId = context.WorkspaceId,
        Definition = new RuntimeProfileProperties { DisplayName = "Shared", RuntimeType = "Local" }
    };

    private sealed class IdentityFixture : IAsyncDisposable
    {
        private readonly string directory;
        private IdentityFixture(string directory, ServiceProvider services)
        {
            this.directory = directory;
            Services = services;
        }

        public ServiceProvider Services { get; }
        public CurrentRequestContext Context => Services.GetRequiredService<CurrentRequestContext>();
        public ILocalEnvironmentBootstrapper Bootstrap => Services.GetRequiredService<ILocalEnvironmentBootstrapper>();

        public static async Task<IdentityFixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), $"agentstration-identity-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAgentstration(
                Path.Combine(directory, "content.json"),
                inMemory: true,
                controlPlaneConnectionString: $"Data Source={Path.Combine(directory, "control-plane.db")}");
            var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<IControlPlaneStore>().InitializeAsync(default);
            await provider.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
            return new IdentityFixture(directory, provider);
        }

        public async Task ExecuteSqlAsync(string sql)
        {
            await using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "control-plane.db")}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
