using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Resources;
using Agentstration.Security.AspNetCoreIdentity;
using Agentstration.Web.Hosting;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
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
    public async Task DisabledUnconfiguredOrEmptyBootstrapHasNoEffect()
    {
        using var directory = new TemporaryDirectory();
        var handler = new RecordingHandler();

        Assert.AreEqual(0, await Service(directory.Path, handler).ApplyAsync(default));
        Assert.AreEqual(
            0,
            await Service(directory.Path, handler, Path.Combine(directory.Path, "missing"), enabled: false).ApplyAsync(default));
        Assert.AreEqual(0, await ServiceForProfiles(directory.Path, handler, []).ApplyAsync(default));
        Assert.AreEqual(0, await Service(directory.Path, handler, directory.Path).ApplyAsync(default));
        Assert.AreEqual(0, handler.Names.Count);
    }

    [TestMethod]
    public async Task MissingInvalidOrDuplicateSelectedProfilesFailClearly()
    {
        using var directory = new TemporaryDirectory();
        var handler = new RecordingHandler();

        var missingRoot = await Assert.ThrowsAsync<DeclarativeBootstrapException>(
            () => ServiceForConfiguration(
                directory.Path,
                handler,
                new Dictionary<string, string?>
                {
                    ["Agentstration:Bootstrap:InitialBootstrapEnabled"] = "true",
                    ["Agentstration:Bootstrap:InitialProfiles:0"] = "development"
                }).ApplyAsync(default));
        StringAssert.Contains(missingRoot.Message, "Agentstration:Bootstrap:Path is required");

        var missing = await Assert.ThrowsAsync<DeclarativeBootstrapException>(
            () => ServiceForProfiles(directory.Path, handler, ["missing"]).ApplyAsync(default));
        StringAssert.Contains(missing.Message, "profile 'missing' does not exist");

        var invalid = await Assert.ThrowsAsync<DeclarativeBootstrapException>(
            () => ServiceForProfiles(directory.Path, handler, ["../outside"]).ApplyAsync(default));
        StringAssert.Contains(invalid.Message, "single valid directory name");

        Directory.CreateDirectory(Path.Combine(directory.Path, "development"));
        var duplicate = await Assert.ThrowsAsync<DeclarativeBootstrapException>(
            () => ServiceForProfiles(directory.Path, handler, ["development", "development"]).ApplyAsync(default));
        StringAssert.Contains(duplicate.Message, "configured more than once");
    }

    [TestMethod]
    public async Task CatalogListingRejectsAnUnboundedNumberOfProfiles()
    {
        using var directory = new TemporaryDirectory();
        for (var index = 0; index < 129; index++)
            Directory.CreateDirectory(Path.Combine(directory.Path, $"profile-{index:D3}"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agentstration:Bootstrap:Path"] = directory.Path
        }).Build();
        var catalog = new BootstrapProfileCatalog(configuration, new TestHostEnvironment(directory.Path));

        var snapshot = await catalog.GetSnapshotAsync(default);

        StringAssert.Contains(snapshot.Error, "more than 128 profiles");
        Assert.HasCount(0, snapshot.Profiles);
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
        StringAssert.Contains(unknown.Message, "Unknown bootstrap resource kind 'Unknown'");

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
    public async Task ProfilesAreAppliedInConfiguredOrderAndCanBeAppliedWhenInitialBootstrapIsDisabled()
    {
        using var directory = new TemporaryDirectory();
        var baseProfile = Directory.CreateDirectory(Path.Combine(directory.Path, "base"));
        var developmentProfile = Directory.CreateDirectory(Path.Combine(directory.Path, "development"));
        await File.WriteAllTextAsync(Path.Combine(baseProfile.FullName, "20-second.yaml"), Resource("Recording", "base"));
        await File.WriteAllTextAsync(Path.Combine(developmentProfile.FullName, "10-first.yaml"), Resource("Recording", "development"));
        var handler = new RecordingHandler();
        var service = ServiceForProfiles(
            directory.Path,
            handler,
            ["development", "base"],
            enabled: false);

        Assert.AreEqual(0, await service.ApplyAsync(default));
        Assert.AreEqual(2, await service.ApplyProfilesAsync(["development", "base"], default));
        CollectionAssert.AreEqual(new[] { "development", "base" }, handler.Names);
    }

    [TestMethod]
    public async Task ProfileDescriptorDefinesScopeAndRequiresAnExplicitMatchingTarget()
    {
        using var directory = new TemporaryDirectory();
        var profile = Directory.CreateDirectory(Path.Combine(directory.Path, "workspace-tools"));
        await File.WriteAllTextAsync(Path.Combine(profile.FullName, "profile.yaml"), Profile("workspace-tools", "workspace"));
        await File.WriteAllTextAsync(Path.Combine(profile.FullName, "10-resource.yaml"), Resource("Recording", "tooling"));
        var handler = new RecordingHandler(BootstrapProfileScope.Workspace);
        var service = ServiceForProfiles(directory.Path, handler, ["workspace-tools"], enabled: false);

        var missingTarget = await Assert.ThrowsAsync<DeclarativeBootstrapException>(
            () => service.PreviewAsync(new(["workspace-tools"]), default));
        StringAssert.Contains(missingTarget.Message, "require a Tenant and Workspace target");

        var target = new BootstrapApplicationTarget(Guid.NewGuid(), Guid.NewGuid());
        var preview = await service.PreviewAsync(new(["workspace-tools"], target), default);

        Assert.AreEqual(BootstrapProfileScope.Workspace, preview.Scope);
        Assert.AreEqual(target, preview.Target);
        Assert.IsTrue(preview.CanApply);
        Assert.AreEqual(BootstrapResourceDisposition.Create, preview.Resources.Single().Disposition);
        Assert.IsFalse(string.IsNullOrWhiteSpace(preview.Digest));
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
    public async Task ManualApplicationRequiresPreviewAndPersistsItsOutcome()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "00-platform-admin.yaml"), PlatformAdministrator());
        await using var factory = Factory(directory.Path, InitialPassword);
        using var client = factory.CreateClient();
        using var health = await client.GetAsync("/health");
        health.EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var account = await scope.ServiceProvider.GetRequiredService<UserManager<LocalIdentityUser>>()
            .FindByNameAsync("bootstrap-admin");
        Assert.IsNotNull(account);
        var principal = await scope.ServiceProvider.GetRequiredService<IPrincipalResolver>()
            .ResolveLocalAsync(account.Id, default);
        Assert.IsNotNull(principal);
        factory.Services.GetRequiredService<IConfiguration>()["Agentstration:Bootstrap:Secrets:AdminPassword"] = null;
        var selection = new BootstrapProfileSelection([Path.GetFileName(directory.Path)]);
        var management = scope.ServiceProvider.GetRequiredService<BootstrapProfileManagementService>();

        var preview = await management.PreviewAsync(selection, principal.Id, default);
        Assert.IsTrue(preview.CanApply);
        Assert.IsTrue(preview.Resources.All(resource => resource.Disposition == BootstrapResourceDisposition.Skip));
        var application = await management.ApplyAsync(selection, preview.Digest, principal.Id, default);

        Assert.AreEqual(BootstrapApplicationStatus.Succeeded, application.Definition.Status);
        Assert.IsFalse(string.IsNullOrWhiteSpace(application.ETag));
        Assert.IsTrue(application.Definition.Resources.All(resource => resource.Disposition == BootstrapResourceDisposition.Skip));
        using var systemScope = scope.ServiceProvider.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var history = await scope.ServiceProvider.GetRequiredService<IControlPlaneStore>()
            .ListAllAsync<BootstrapApplicationResource>(ResourceKinds.BootstrapApplication, default);
        Assert.AreEqual(application.Metadata.Name, history.Single().Value.Metadata.Name);
        Assert.AreEqual(
            application.Metadata.Name,
            (await management.GetApplicationAsync(application.Metadata.Name, principal.Id, default))?.Metadata.Name);

        var staleName = Guid.NewGuid().ToString("N");
        var stale = application with
        {
            Metadata = application.Metadata with { Name = staleName },
            ETag = null,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Creating },
            Definition = application.Definition with
            {
                Status = BootstrapApplicationStatus.Running,
                CompletedAt = null,
                Error = null,
                Resources = []
            }
        };
        _ = await scope.ServiceProvider.GetRequiredService<IControlPlaneStore>().PutAsync(stale, null, true, default);
        var recovered = await management.GetApplicationAsync(staleName, principal.Id, default);
        Assert.AreEqual(BootstrapApplicationStatus.Interrupted, recovered?.Definition.Status);
        Assert.IsNotNull(recovered?.Definition.CompletedAt);
    }

    [TestMethod]
    public async Task InterruptedManualApplicationIsFinalizedInHistory()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "00-platform-admin.yaml"), PlatformAdministrator());
        await using var factory = Factory(directory.Path, InitialPassword, services =>
            services.AddScoped<IBootstrapResourceHandler, InterruptingHandler>());
        using var client = factory.CreateClient();
        using var health = await client.GetAsync("/health");
        health.EnsureSuccessStatusCode();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "99-interrupt.yaml"), Resource("Interrupting", "stop"));

        await using var scope = factory.Services.CreateAsyncScope();
        var account = await scope.ServiceProvider.GetRequiredService<UserManager<LocalIdentityUser>>()
            .FindByNameAsync("bootstrap-admin");
        Assert.IsNotNull(account);
        var principal = await scope.ServiceProvider.GetRequiredService<IPrincipalResolver>()
            .ResolveLocalAsync(account.Id, default);
        Assert.IsNotNull(principal);
        var management = scope.ServiceProvider.GetRequiredService<BootstrapProfileManagementService>();
        var selection = new BootstrapProfileSelection([Path.GetFileName(directory.Path)]);
        var preview = await management.PreviewAsync(selection, principal.Id, default);

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => management.ApplyAsync(selection, preview.Digest, principal.Id, default));

        using var systemScope = scope.ServiceProvider.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var application = (await scope.ServiceProvider.GetRequiredService<IControlPlaneStore>()
            .ListAllAsync<BootstrapApplicationResource>(ResourceKinds.BootstrapApplication, default)).Single().Value;
        Assert.AreEqual(BootstrapApplicationStatus.Interrupted, application.Definition.Status);
        Assert.IsNotNull(application.Definition.CompletedAt);
        Assert.IsFalse(string.IsNullOrWhiteSpace(application.Definition.Error));
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
    public async Task WorkspaceResourcesCanBeAppliedDirectlyAndRemainIndependentFromPacks()
    {
        using var directory = new TemporaryDirectory();
        var initial = Directory.CreateDirectory(Path.Combine(directory.Path, "initial"));
        var resources = Directory.CreateDirectory(Path.Combine(directory.Path, "workspace-resources"));
        await File.WriteAllTextAsync(Path.Combine(initial.FullName, "00-platform-admin.yaml"), PlatformAdministrator());
        await File.WriteAllTextAsync(Path.Combine(initial.FullName, "10-tenant.yaml"), Tenant("dev", "Development"));
        await File.WriteAllTextAsync(Path.Combine(initial.FullName, "20-workspace.yaml"), Workspace("default", "Default workspace", "dev"));
        await File.WriteAllTextAsync(Path.Combine(initial.FullName, "30-default-context.yaml"), DefaultContext("bootstrap-admin", "dev", "default"));
        await File.WriteAllTextAsync(Path.Combine(resources.FullName, "profile.yaml"), Profile("workspace-resources", "workspace"));
        await File.WriteAllTextAsync(Path.Combine(resources.FullName, "10-model-provider.yaml"), ModelProvider());
        await File.WriteAllTextAsync(Path.Combine(resources.FullName, "20-runtime-profile.yaml"), RuntimeProfile());
        await File.WriteAllTextAsync(Path.Combine(resources.FullName, "30-model-profile.yaml"), ModelProfile());
        await File.WriteAllTextAsync(Path.Combine(resources.FullName, "40-agent.yaml"), Agent());
        await File.WriteAllTextAsync(Path.Combine(resources.FullName, "50-flow.yaml"), Flow());
        await File.WriteAllTextAsync(Path.Combine(resources.FullName, "60-entry.yaml"), Entry());
        await using var factory = Factory(initial.FullName, InitialPassword, configureOllamaExtension: true);
        using var client = factory.CreateClient();
        using var health = await client.GetAsync("/health");
        health.EnsureSuccessStatusCode();

        await using var scope = factory.Services.CreateAsyncScope();
        var account = await scope.ServiceProvider.GetRequiredService<UserManager<LocalIdentityUser>>()
            .FindByNameAsync("bootstrap-admin");
        Assert.IsNotNull(account);
        var principal = await scope.ServiceProvider.GetRequiredService<IPrincipalResolver>()
            .ResolveLocalAsync(account.Id, default);
        Assert.IsNotNull(principal);
        var identities = scope.ServiceProvider.GetRequiredService<IIdentityStore>();
        var tenant = await identities.FindTenantByNameAsync("dev", default);
        Assert.IsNotNull(tenant);
        var workspace = await identities.FindWorkspaceByNameAsync(tenant.Id, "default", default);
        Assert.IsNotNull(workspace);
        using (scope.ServiceProvider.GetRequiredService<IRequestContextScopeFactory>()
            .Push(new RequestContext(principal.Id, tenant.Id, workspace.Id)))
        {
            _ = await scope.ServiceProvider.GetRequiredService<ExtensionSourceDiscoveryService>().DiscoverAsync(default);
        }
        var selection = new BootstrapProfileSelection(
            ["workspace-resources"],
            new(tenant.Id, workspace.Id));
        var management = scope.ServiceProvider.GetRequiredService<BootstrapProfileManagementService>();

        var preview = await management.PreviewAsync(selection, principal.Id, default);

        Assert.IsTrue(
            preview.CanApply,
            string.Join(Environment.NewLine, preview.Resources.Select(resource => $"{resource.Kind}/{resource.Name}: {resource.Message}")));
        Assert.HasCount(6, preview.Resources);
        Assert.IsTrue(preview.Resources.All(resource => resource.Disposition == BootstrapResourceDisposition.Create));
        var application = await management.ApplyAsync(selection, preview.Digest, principal.Id, default);
        Assert.AreEqual(BootstrapApplicationStatus.Succeeded, application.Definition.Status);
        Assert.HasCount(6, application.Definition.Resources);
        Assert.IsTrue(application.Definition.Resources.All(resource => resource.Disposition == BootstrapResourceDisposition.Create));

        using (scope.ServiceProvider.GetRequiredService<IRequestContextScopeFactory>()
            .Push(new RequestContext(principal.Id, tenant.Id, workspace.Id)))
        {
            var provider = await scope.ServiceProvider.GetRequiredService<ModelProviderManagementService>()
                .GetAsync("bootstrap-provider", default);
            var runtime = await scope.ServiceProvider.GetRequiredService<RuntimeProfileManagementService>()
                .GetAsync("bootstrap-runtime", default);
            var model = await scope.ServiceProvider.GetRequiredService<ModelProfileManagementService>()
                .GetAsync("bootstrap-model", default);
            var agent = await scope.ServiceProvider.GetRequiredService<AgentManagementService>()
                .GetAgentAsync("bootstrap-agent", default);
            var flow = await scope.ServiceProvider.GetRequiredService<FlowService>()
                .GetAsync(new(workspace.Id), new("bootstrap-flow"), default);
            var entry = await scope.ServiceProvider.GetRequiredService<IWorkplaceRepository>()
                .GetEntryAsync(new(workspace.Id), new("bootstrap-entry"), default);

            Assert.IsNotNull(provider);
            Assert.IsNotNull(runtime);
            Assert.IsNotNull(model);
            Assert.IsNotNull(agent);
            Assert.IsNotNull(flow);
            Assert.IsNotNull(entry);
            Assert.IsTrue(flow.Value.ActiveVersion is not null);
            Assert.IsFalse(provider.Value.Metadata.Annotations.ContainsKey(PackProvenanceAnnotations.Name));
            Assert.IsFalse(runtime.Value.Metadata.Annotations.ContainsKey(PackProvenanceAnnotations.Name));
            Assert.IsFalse(model.Value.Metadata.Annotations.ContainsKey(PackProvenanceAnnotations.Name));
            Assert.IsFalse(agent.Value.Metadata.Annotations.ContainsKey(PackProvenanceAnnotations.Name));
            Assert.IsFalse(flow.Value.Metadata.ContainsKey(PackProvenanceAnnotations.Name));
        }

        var secondPreview = await management.PreviewAsync(selection, principal.Id, default);
        Assert.IsTrue(secondPreview.Resources.All(resource => resource.Disposition == BootstrapResourceDisposition.Skip));
        var secondApplication = await management.ApplyAsync(selection, secondPreview.Digest, principal.Id, default);
        Assert.AreEqual(BootstrapApplicationStatus.Succeeded, secondApplication.Definition.Status);
        Assert.IsTrue(secondApplication.Definition.Resources.All(resource => resource.Disposition == BootstrapResourceDisposition.Skip));

        var inactive = Directory.CreateDirectory(Path.Combine(directory.Path, "inactive-flow"));
        await File.WriteAllTextAsync(Path.Combine(inactive.FullName, "profile.yaml"), Profile("inactive-flow", "workspace"));
        await File.WriteAllTextAsync(Path.Combine(inactive.FullName, "10-flow.yaml"), InactiveFlow());
        await File.WriteAllTextAsync(Path.Combine(inactive.FullName, "20-entry.yaml"), InactiveFlowEntry());
        var invalidPreview = await management.PreviewAsync(
            new(["inactive-flow"], selection.Target),
            principal.Id,
            default);
        Assert.AreEqual(BootstrapResourceDisposition.Create, invalidPreview.Resources[0].Disposition);
        Assert.AreEqual(BootstrapResourceDisposition.Invalid, invalidPreview.Resources[1].Disposition);
        StringAssert.Contains(invalidPreview.Resources[1].Message, "no active published version");
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

    private static DeclarativeBootstrapService Service(
        string contentRoot,
        IBootstrapResourceHandler handler,
        string? path = null,
        bool enabled = true)
    {
        if (path is null)
            return ServiceForConfiguration(contentRoot, handler, null);

        var root = Directory.GetParent(path)?.FullName
            ?? throw new InvalidOperationException($"Bootstrap test path '{path}' has no parent directory.");
        var profile = Path.GetFileName(path);
        return ServiceForProfiles(root, handler, [profile], enabled);
    }

    private static DeclarativeBootstrapService ServiceForProfiles(
        string root,
        IBootstrapResourceHandler handler,
        IReadOnlyList<string> profiles,
        bool enabled = true)
    {
        var values = new Dictionary<string, string?>
        {
            ["Agentstration:Bootstrap:Path"] = root,
            ["Agentstration:Bootstrap:InitialBootstrapEnabled"] = enabled.ToString()
        };
        for (var index = 0; index < profiles.Count; index++)
            values[$"Agentstration:Bootstrap:InitialProfiles:{index}"] = profiles[index];

        return ServiceForConfiguration(root, handler, values);
    }

    private static DeclarativeBootstrapService ServiceForConfiguration(
        string contentRoot,
        IBootstrapResourceHandler handler,
        IEnumerable<KeyValuePair<string, string?>>? values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var environment = new TestHostEnvironment(contentRoot);
        var catalog = new BootstrapProfileCatalog(configuration, environment);
        return new(configuration, catalog, [handler], NullLogger<DeclarativeBootstrapService>.Instance);
    }

    private static WebApplicationFactory<Program> Factory(
        string path,
        string? password,
        Action<IServiceCollection>? configureServices = null,
        bool configureOllamaExtension = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Agentstration:Authentication:Mode", "Local");
            builder.UseSetting("Agentstration:Bootstrap:Path", Directory.GetParent(path)!.FullName);
            builder.UseSetting("Agentstration:Bootstrap:InitialBootstrapEnabled", "true");
            builder.UseSetting("Agentstration:Bootstrap:InitialProfiles:0", Path.GetFileName(path));
            if (configureOllamaExtension)
                builder.UseSetting("Agentstration:Extensions:Agentstration.Extensions.Ollama:Endpoint", "http://127.0.0.1:1");
            if (password is not null) builder.UseSetting("Agentstration:Bootstrap:Secrets:AdminPassword", password);
            if (configureServices is not null) builder.ConfigureServices(configureServices);
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

    private static string Profile(string name, string targetScope) => $$"""
        apiVersion: {{ManagementApiVersions.CoreV1}}
        kind: BootstrapProfile
        metadata:
          name: {{name}}
        definition:
          displayName: {{name}}
          targetScope: {{targetScope}}
        """;

    private static string ModelProvider() => $$"""
        apiVersion: {{ManagementApiVersions.CoreV1}}
        kind: ModelProvider
        metadata:
          name: bootstrap-provider
        definition:
          displayName: Bootstrap provider
          extension:
            name: ollama-extension
          contributionId: ollama
        """;

    private static string RuntimeProfile() => $$"""
        apiVersion: {{ManagementApiVersions.CoreV1}}
        kind: RuntimeProfile
        metadata:
          name: bootstrap-runtime
        definition:
          displayName: Bootstrap runtime
          runtimeType: microsoft-agent-framework
          execution:
            sessionMode: transient
            toolInvocation: automatic
            streaming: automatic
        """;

    private static string ModelProfile() => $$"""
        apiVersion: {{ManagementApiVersions.CoreV1}}
        kind: ModelProfile
        metadata:
          name: bootstrap-model
        definition:
          displayName: Bootstrap model
          provider:
            name: bootstrap-provider
          model:
            name: qwen3:1.7b
          generation:
            temperature: 0.2
        """;

    private static string Agent() => $$"""
        apiVersion: {{ManagementApiVersions.CoreV1}}
        kind: Agent
        metadata:
          name: bootstrap-agent
        definition:
          displayName: Bootstrap agent
          instructions: Answer concisely.
          modelProfile:
            name: bootstrap-model
          runtimeProfile:
            name: bootstrap-runtime
          tools: []
        """;

    private static string Flow() => $$"""
        apiVersion: {{ManagementApiVersions.CoreV1}}
        kind: Flow
        metadata:
          name: bootstrap-flow
        definition:
          displayName: Bootstrap flow
          version: 1.0.0
          enabled: true
          spec:
            flowKind: direct
            target:
              kind: agent
              id: bootstrap-agent
          publish: true
          activate: true
        """;

    private static string Entry() => $$"""
        apiVersion: {{ManagementApiVersions.CoreV1}}
        kind: Entry
        metadata:
          name: bootstrap-entry
        definition:
          displayName: Bootstrap entry
          presentation:
            kind: prompt
            placeholder: Ask something
            allowAttachments: false
            allowVoiceInput: false
            fields:
              - name: request
                type: prompt
                label: Request
                required: true
                order: 0
                role: primaryInput
          binding:
            kind: flow
            resourceId: bootstrap-flow
          behavior:
            taskCreationMode: automatic
            allowConversation: true
            streamResponse: true
          publish: true
        """;

    private static string InactiveFlow() => $$"""
        apiVersion: {{ManagementApiVersions.CoreV1}}
        kind: Flow
        metadata:
          name: inactive-flow
        definition:
          displayName: Inactive flow
          version: 1.0.0
          enabled: true
          spec:
            flowKind: direct
            target:
              kind: agent
              id: bootstrap-agent
          publish: false
          activate: false
        """;

    private static string InactiveFlowEntry() => $$"""
        apiVersion: {{ManagementApiVersions.CoreV1}}
        kind: Entry
        metadata:
          name: inactive-flow-entry
        definition:
          displayName: Inactive flow entry
          presentation:
            kind: prompt
            placeholder: Ask something
            allowAttachments: false
            allowVoiceInput: false
            fields:
              - name: request
                type: prompt
                label: Request
                required: true
                order: 0
                role: primaryInput
          binding:
            kind: flow
            resourceId: inactive-flow
          behavior:
            taskCreationMode: automatic
          publish: true
        """;

    private sealed class RecordingHandler(BootstrapProfileScope scope = BootstrapProfileScope.Instance) : IBootstrapResourceHandler
    {
        public string Kind => "Recording";
        public BootstrapProfileScope Scope { get; } = scope;
        public List<string> Names { get; } = [];

        public Task<BootstrapResourcePlanResult> PlanAsync(
            BootstrapResourceDocument resource,
            BootstrapResourceOperationContext operation,
            BootstrapPlanningContext planning,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BootstrapResourcePlanResult(BootstrapResourceDisposition.Create));

        public Task<BootstrapResourceApplyResult> ApplyAsync(
            BootstrapResourceDocument resource,
            BootstrapResourceOperationContext operation,
            CancellationToken cancellationToken)
        {
            Names.Add(resource.Metadata.Name);
            return Task.FromResult(BootstrapResourceApplyResult.Created);
        }
    }

    private sealed class InterruptingHandler : IBootstrapResourceHandler
    {
        public string Kind => "Interrupting";
        public BootstrapProfileScope Scope => BootstrapProfileScope.Instance;

        public Task<BootstrapResourcePlanResult> PlanAsync(
            BootstrapResourceDocument resource,
            BootstrapResourceOperationContext operation,
            BootstrapPlanningContext planning,
            CancellationToken cancellationToken) =>
            Task.FromResult(new BootstrapResourcePlanResult(BootstrapResourceDisposition.Create));

        public Task<BootstrapResourceApplyResult> ApplyAsync(
            BootstrapResourceDocument resource,
            BootstrapResourceOperationContext operation,
            CancellationToken cancellationToken) =>
            throw new OperationCanceledException(cancellationToken);
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
