using Agentstration.Management.Abstractions;
using Agentstration.Security.AspNetCoreIdentity;
using Agentstration.Web.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class DeclarativeBootstrapTests
{
    private const string InitialPassword = "Initial123!Password";
    private const string ChangedPassword = "Changed123!Password";

    [TestMethod]
    public async Task MissingUnconfiguredOrEmptyDirectoriesHaveNoEffect()
    {
        using var directory = new TemporaryDirectory();
        var handler = new RecordingHandler();

        Assert.AreEqual(0, await Service(directory.Path, handler).ApplyAsync(default));
        Assert.AreEqual(0, await Service(directory.Path, handler, Path.Combine(directory.Path, "missing")).ApplyAsync(default));
        Assert.AreEqual(0, await Service(directory.Path, handler, directory.Path).ApplyAsync(default));
        Assert.AreEqual(0, handler.Names.Count);
    }

    [TestMethod]
    public async Task InvalidYamlUnknownKindAndUnsupportedApiVersionFailClearly()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "00-invalid.yaml"), "apiVersion: [");
        var invalid = await Assert.ThrowsAsync<DeclarativeBootstrapException>(
            () => Service(directory.Path, new RecordingHandler(), directory.Path).ApplyAsync(default));
        StringAssert.Contains(invalid.Message, "invalid YAML");

        File.Delete(Path.Combine(directory.Path, "00-invalid.yaml"));
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "00-unknown.yaml"), Resource("Unknown", "one"));
        var unknown = await Assert.ThrowsAsync<DeclarativeBootstrapException>(
            () => Service(directory.Path, new RecordingHandler(), directory.Path).ApplyAsync(default));
        StringAssert.Contains(unknown.Message, "unknown kind 'Unknown'");

        File.Delete(Path.Combine(directory.Path, "00-unknown.yaml"));
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "00-version.yaml"),
            Resource("Recording", "one").Replace(ManagementApiVersions.CoreV1, "agentstration.io/v2", StringComparison.Ordinal));
        var unsupported = await Assert.ThrowsAsync<DeclarativeBootstrapException>(
            () => Service(directory.Path, new RecordingHandler(), directory.Path).ApplyAsync(default));
        StringAssert.Contains(unsupported.Message, "unsupported apiVersion 'agentstration.io/v2'");
    }

    [TestMethod]
    public async Task FilesAndDocumentsAreAppliedInDeterministicOrder()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "20-last.yml"), Resource("Recording", "last"));
        await File.WriteAllTextAsync(
            Path.Combine(directory.Path, "10-first.yaml"),
            $"{Resource("Recording", "first")}\n---\n{Resource("Recording", "second")}");
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "ignored.json"), "{}");
        var handler = new RecordingHandler();

        Assert.AreEqual(3, await Service(directory.Path, handler, directory.Path).ApplyAsync(default));
        CollectionAssert.AreEqual(new[] { "first", "second", "last" }, handler.Names);
    }

    [TestMethod]
    public async Task PlatformAdministratorIsCreatedOnceAndBootstrapDoesNotResetChangedPassword()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "00-platform-admin.yaml"), PlatformAdministrator());
        await using var factory = Factory(directory.Path, InitialPassword);
        using var client = factory.CreateClient();
        using var health = await client.GetAsync("/health");
        health.EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<LocalIdentityUser>>();
        var account = await users.FindByNameAsync("bootstrap-admin");
        Assert.IsNotNull(account);
        Assert.AreEqual(1, await users.Users.CountAsync());
        Assert.IsTrue(await users.CheckPasswordAsync(account, InitialPassword));
        var principal = await scope.ServiceProvider.GetRequiredService<IPrincipalResolver>().ResolveLocalAsync(account.Id, default);
        Assert.IsNotNull(principal);
        Assert.IsTrue(await scope.ServiceProvider.GetRequiredService<IPlatformAuthorizationService>()
            .IsPlatformAdministratorAsync(principal.Id, default));

        var changed = await users.ChangePasswordAsync(account, InitialPassword, ChangedPassword);
        Assert.IsTrue(changed.Succeeded, string.Join("; ", changed.Errors.Select(error => error.Description)));
        await factory.Services.ApplyDeclarativeBootstrapAsync(default);

        Assert.AreEqual(1, await users.Users.CountAsync());
        Assert.IsTrue(await users.CheckPasswordAsync(account, ChangedPassword));
        Assert.IsFalse(await users.CheckPasswordAsync(account, InitialPassword));
    }

    [TestMethod]
    public async Task ExistingPlatformAdministratorIsSkippedWithoutResolvingTheSecretAgain()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "00-platform-admin.yaml"), PlatformAdministrator());
        await using var factory = Factory(directory.Path, InitialPassword);
        using var client = factory.CreateClient();
        using var health = await client.GetAsync("/health");
        health.EnsureSuccessStatusCode();

        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        configuration["Agentstration:Bootstrap:Secrets:AdminPassword"] = null;

        await factory.Services.ApplyDeclarativeBootstrapAsync(default);

        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<LocalIdentityUser>>();
        var account = await users.FindByNameAsync("bootstrap-admin");
        Assert.IsNotNull(account);
        Assert.AreEqual(1, await users.Users.CountAsync());
        Assert.IsTrue(await users.CheckPasswordAsync(account, InitialPassword));
    }

    [TestMethod]
    public async Task CompleteTopologyIsDeclarativeAndPlatformAdministratorHasGlobalAccessWithoutMemberships()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "00-platform-admin.yaml"), PlatformAdministrator());
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "10-dev-tenant.yaml"), Tenant("dev", "Development"));
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "11-customer-tenant.yaml"), Tenant("customer", "Customer"));
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "20-default-workspace.yaml"), Workspace("default", "Default workspace", "dev"));
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "21-customer-workspace.yaml"), Workspace("operations", "Operations", "customer"));
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "30-default-context.yaml"), DefaultContext("bootstrap-admin", "dev", "default"));
        await using var factory = Factory(directory.Path, InitialPassword);
        using var client = factory.CreateClient();
        using var health = await client.GetAsync("/health");
        health.EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IIdentityStore>();
        var account = await scope.ServiceProvider.GetRequiredService<UserManager<LocalIdentityUser>>()
            .FindByNameAsync("bootstrap-admin");
        Assert.IsNotNull(account);
        var principal = await scope.ServiceProvider.GetRequiredService<IPrincipalResolver>()
            .ResolveLocalAsync(account.Id, default);
        Assert.IsNotNull(principal);
        var dev = await store.FindTenantByNameAsync("dev", default);
        var customer = await store.FindTenantByNameAsync("customer", default);
        Assert.IsNotNull(dev);
        Assert.IsNotNull(customer);
        var defaultWorkspace = await store.FindWorkspaceByNameAsync(dev.Id, "default", default);
        var operations = await store.FindWorkspaceByNameAsync(customer.Id, "operations", default);
        Assert.IsNotNull(defaultWorkspace);
        Assert.IsNotNull(operations);

        var preferences = await store.GetPrincipalPreferencesAsync(principal.Id, default);
        Assert.IsNotNull(preferences);
        Assert.AreEqual(dev.Id, preferences.DefaultTenantId);
        Assert.AreEqual(defaultWorkspace.Id, preferences.DefaultWorkspaceId);
        Assert.AreEqual(0, (await store.ListMembershipsAsync(dev.Id, default)).Count);
        Assert.AreEqual(0, (await store.ListMembershipsAsync(customer.Id, default)).Count);
        Assert.AreEqual(0, (await store.ListWorkspaceMembershipsAsync(principal.Id, default)).Count);
        Assert.AreEqual(0, (await store.ListRoleAssignmentsAsync(dev.Id, principal.Id, default)).Count);
        Assert.AreEqual(0, (await store.ListRoleAssignmentsAsync(customer.Id, principal.Id, default)).Count);

        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        Assert.IsTrue(await authorization.HasPermissionAsync(
            new RequestContext(principal.Id, customer.Id, operations.Id),
            AuthorizationPermissions.WorkspacesWrite,
            default));

        factory.Services.GetRequiredService<IConfiguration>()["Agentstration:Bootstrap:Secrets:AdminPassword"] = null;
        Assert.AreEqual(6, await scope.ServiceProvider.GetRequiredService<DeclarativeBootstrapService>().ApplyAsync(default));
    }

    [TestMethod]
    public async Task ExistingNonAdministratorAccountIsANonFatalConflictWithoutResolvingTheSecret()
    {
        using var directory = new TemporaryDirectory();
        await using var factory = Factory(directory.Path, InitialPassword);
        using var client = factory.CreateClient();
        using var health = await client.GetAsync("/health");
        health.EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<LocalIdentityUser>>();
        var account = new LocalIdentityUser { UserName = "bootstrap-admin" };
        var created = await users.CreateAsync(account, ChangedPassword);
        Assert.IsTrue(created.Succeeded, string.Join("; ", created.Errors.Select(error => error.Description)));
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "00-platform-admin.yaml"), PlatformAdministrator());
        factory.Services.GetRequiredService<IConfiguration>()["Agentstration:Bootstrap:Secrets:AdminPassword"] = null;

        var result = await scope.ServiceProvider.GetRequiredService<DeclarativeBootstrapService>().ApplyAsync(default);

        Assert.AreEqual(1, result);
        Assert.IsTrue(await users.CheckPasswordAsync(account, ChangedPassword));
        var principal = await scope.ServiceProvider.GetRequiredService<IPrincipalResolver>().ResolveLocalAsync(account.Id, default);
        Assert.IsNull(principal);
    }

    [TestMethod]
    public async Task MissingReferencedConfigurationFailsStartup()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "00-platform-admin.yaml"), PlatformAdministrator());
        await using var factory = Factory(directory.Path, null);

        var exception = await Assert.ThrowsAsync<DeclarativeBootstrapException>(() => Task.Run(() => factory.CreateClient()));
        StringAssert.Contains(exception.Message, "Agentstration:Bootstrap:Secrets:AdminPassword");
        Assert.DoesNotContain(InitialPassword, exception.ToString(), StringComparison.Ordinal);
    }

    [TestMethod]
    public void DevelopmentIdentityPolicyAcceptsThePublicBootstrapCredentialOnlyWhenRequested()
    {
        using var directory = new TemporaryDirectory();
        var developmentServices = new ServiceCollection();
        developmentServices.AddLogging();
        developmentServices.AddAgentstrationLocalIdentity(
            "Data Source=:memory:",
            directory.Path,
            useDevelopmentPasswordPolicy: true);
        using var development = developmentServices.BuildServiceProvider();
        var developmentPolicy = development.GetRequiredService<IOptions<IdentityOptions>>().Value.Password;
        Assert.AreEqual(5, developmentPolicy.RequiredLength);
        Assert.IsFalse(developmentPolicy.RequireDigit);
        Assert.IsFalse(developmentPolicy.RequireLowercase);
        Assert.IsFalse(developmentPolicy.RequireUppercase);
        Assert.IsFalse(developmentPolicy.RequireNonAlphanumeric);

        var defaultServices = new ServiceCollection();
        defaultServices.AddLogging();
        defaultServices.AddAgentstrationLocalIdentity("Data Source=:memory:", Path.Combine(directory.Path, "default-keys"));
        using var defaults = defaultServices.BuildServiceProvider();
        var defaultPolicy = defaults.GetRequiredService<IOptions<IdentityOptions>>().Value.Password;
        Assert.AreEqual(12, defaultPolicy.RequiredLength);
        Assert.IsTrue(defaultPolicy.RequireDigit);
        Assert.IsTrue(defaultPolicy.RequireLowercase);
        Assert.IsTrue(defaultPolicy.RequireUppercase);
        Assert.IsTrue(defaultPolicy.RequireNonAlphanumeric);
    }

    private static DeclarativeBootstrapService Service(string contentRoot, IBootstrapResourceHandler handler, string? path = null)
    {
        var values = path is null ? null : new Dictionary<string, string?> { ["Agentstration:Bootstrap:Path"] = path };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new(configuration, new TestHostEnvironment(contentRoot), [handler], NullLogger<DeclarativeBootstrapService>.Instance);
    }

    private static WebApplicationFactory<Program> Factory(string path, string? password) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Agentstration:Authentication:Mode", "Local");
            builder.UseSetting("Agentstration:Bootstrap:Path", path);
            if (password is not null) builder.UseSetting("Agentstration:Bootstrap:Secrets:AdminPassword", password);
        });

    private static string PlatformAdministrator() => $$"""
        apiVersion: {{ManagementApiVersions.CoreV1}}
        kind: PlatformAdministrator
        metadata:
          name: bootstrap-admin
        definition:
          displayName: Bootstrap administrator
          email: bootstrap@example.test
          passwordFrom:
            configuration: Agentstration:Bootstrap:Secrets:AdminPassword
        """;

    private static string Tenant(string name, string displayName) => $$"""
        apiVersion: {{ManagementApiVersions.CoreV1}}
        kind: Tenant
        metadata:
          name: {{name}}
        definition:
          displayName: {{displayName}}
        """;

    private static string Workspace(string name, string displayName, string tenantName) => $$"""
        apiVersion: {{ManagementApiVersions.CoreV1}}
        kind: Workspace
        metadata:
          name: {{name}}
        definition:
          displayName: {{displayName}}
          tenantRef:
            name: {{tenantName}}
        """;

    private static string DefaultContext(string localAccount, string tenantName, string workspaceName) => $$"""
        apiVersion: {{ManagementApiVersions.CoreV1}}
        kind: PrincipalDefaultContext
        metadata:
          name: {{localAccount}}
        definition:
          principalRef:
            localAccount: {{localAccount}}
          tenantRef:
            name: {{tenantName}}
          workspaceRef:
            name: {{workspaceName}}
        """;

    private static string Resource(string kind, string name) => $$"""
        apiVersion: {{ManagementApiVersions.CoreV1}}
        kind: {{kind}}
        metadata:
          name: {{name}}
        definition: {}
        """;

    private sealed class RecordingHandler : IBootstrapResourceHandler
    {
        public string Kind => "Recording";
        public List<string> Names { get; } = [];
        public Task<BootstrapResourceApplyResult> ApplyAsync(
            BootstrapResourceDocument resource,
            CancellationToken cancellationToken)
        {
            Names.Add(resource.Metadata.Name);
            return Task.FromResult(BootstrapResourceApplyResult.Created);
        }
    }

    private sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Agentstration.Management.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"agentstration-bootstrap-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
