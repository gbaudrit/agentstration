using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.Parameters;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using static Agentstration.Management.Tests.BootstrapApplicationTestFixture;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed partial class DeclarativeBootstrapTests
{
    [TestMethod]
    public async Task MixedPlanPreviewsWithoutWritesThenCreatesItsTargetsAndResolvesProducerBindings()
    {
        using var catalog = new PlanCatalog();
        await catalog.WriteAsync();
        await using var factory = PlanFactory(catalog);
        using var client = factory.CreateClient();
        _ = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        var applications = FuturePlan();
        using var optionsResponse = await client.PostAsJsonAsync("/api/bootstrap/binding-targets", new BootstrapBindingTargetsRequest(
            null, BootstrapBindingTargetKind.Parameter, [], Applications: applications, ConsumerProfile: "workspace"));
        optionsResponse.EnsureSuccessStatusCode();
        var options = await optionsResponse.Content.ReadFromJsonAsync<BootstrapBindingTargetOption[]>();
        Assert.IsTrue(options!.Any(option => option.Name == "plan-origin" && option.TargetProfile == "tenant"));
        using var response = await client.PostAsJsonAsync("/api/bootstrap/profiles/preview", new BootstrapProfilePreviewRequest([], Applications: applications));
        response.EnsureSuccessStatusCode();
        var preview = (await response.Content.ReadFromJsonAsync<BootstrapCompositionPreview>())!;
        CollectionAssert.AreEqual(new[] { "instance", "tenant", "workspace" }, preview.Profiles.Select(profile => profile.Name).ToArray());
        Assert.IsTrue(preview.CanApply);
        Assert.IsTrue(preview.Applications.Single(item => item.Profile == "workspace").ValidationDeferred);
        await using var scope = factory.Services.CreateAsyncScope();
        var identities = scope.ServiceProvider.GetRequiredService<IIdentityStore>();
        Assert.IsNull(await identities.FindTenantByNameAsync("plan-tenant", default), "Preview must never create its future targets.");
        var request = new ApplyBootstrapProfilesRequest([], preview.Digest, RequestId: Guid.NewGuid(), Applications: applications);
        using var accepted = await client.PostAsJsonAsync("/api/bootstrap/applications", request);
        Assert.AreEqual(HttpStatusCode.Accepted, accepted.StatusCode);
        var application = (await accepted.Content.ReadFromJsonAsync<BootstrapApplicationResource>())!;
        using var repeated = await client.PostAsJsonAsync("/api/bootstrap/applications", request);
        repeated.EnsureSuccessStatusCode();
        Assert.AreEqual(application.Metadata.Name, (await repeated.Content.ReadFromJsonAsync<BootstrapApplicationResource>())!.Metadata.Name);
        var done = await WaitForApplicationAsync(client, application.Metadata.Name);
        Assert.AreEqual(BootstrapApplicationStatus.Succeeded, done.Definition.Status, done.Definition.Error);
        Assert.HasCount(6, done.Definition.Resources);
        Assert.HasCount(6, done.Definition.ResourcePlan);
        Assert.IsTrue(done.Definition.ResourcePlan.All(item => item.TargetLabel is not null));
        CollectionAssert.AreEqual(new[] { "instance", "tenant", "workspace" }, done.Definition.Profiles.ToArray());
        var tenant = (await identities.FindTenantByNameAsync("plan-tenant", default))!;
        var workspace = await identities.FindWorkspaceByNameAsync(tenant.Id, "plan-workspace", default);
        Assert.IsNotNull(workspace);
        Assert.AreEqual(WorkspaceStatus.Active, workspace.Status);
        Assert.AreEqual(workspace.Id, done.Definition.Applications.Single(item => item.Profile == "workspace").ResolvedTarget?.WorkspaceId);
        using var system = scope.ServiceProvider.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var parameter = await scope.ServiceProvider.GetRequiredService<ParameterManagementService>().GetExactAsync(ResourceScopeRef.Tenant(tenant.Id), "plan-origin", default);
        Assert.IsNotNull(parameter);
        Assert.AreEqual(ResourceScopeRef.Tenant(tenant.Id).Value, catalog.Reference!.Value.GetProperty("scopeRef").GetString());
        Assert.AreEqual("plan-origin", catalog.Reference!.Value.GetProperty("address").GetProperty("name").GetString());
        var recordedProfiles = await client.GetFromJsonAsync<AppliedBootstrapProfile[]>("/api/bootstrap/applied-profiles");
        var targets = new Dictionary<string, ResourceScopeRef>
        {
            ["instance"] = ResourceScopeRef.Instance,
            ["tenant"] = ResourceScopeRef.Tenant(tenant.Id),
            ["workspace"] = ResourceScopeRef.Workspace(workspace.Id)
        };
        foreach (var (profileName, targetScope) in targets)
        {
            var profile = recordedProfiles!.Single(item => item.Name == profileName);
            var recorded = (await HistoryAsync(client, profile.Id, "1.0.0", targetScope)).Single();
            Assert.AreEqual(targetScope, recorded.Scope);
            Assert.AreEqual(Guid.Parse(application.Metadata.Name), recorded.CorrelationId);
            Assert.AreEqual(BootstrapApplicationStatus.Succeeded, recorded.Status);
            Assert.IsTrue(recorded.Resources.All(resource => resource.Profile == profileName && resource.Version == "1.0.0"));
            Assert.HasCount(0, await HistoryAsync(client, profile.Id, "1.0.0",
                targetScope == ResourceScopeRef.Instance ? ResourceScopeRef.Tenant(tenant.Id) : ResourceScopeRef.Instance));
        }
        Assert.AreEqual("1.0.0", parameter.Value.Governance.AppliedBy?.Version);
    }

    [TestMethod]
    public async Task VersionedPackKeepsItsOriginAndIsRecordedIndependentlyInTwoWorkspaces()
    {
        using var catalog = new PlanCatalog();
        await catalog.WriteAsync();
        const string profileName = "pack-profile";
        const string profileDescriptor = "apiVersion: agentstration.io/v1\nkind: PackInstallation\nmetadata: { name: fixture-pack }\ndefinition: { source: { path: pack.zip } }\n";
        await catalog.ProfileAsync(profileName, "workspace", profileDescriptor);
        var profileDir = Path.Combine(catalog.Path, profileName);
        var fixtureFlow = await File.ReadAllTextAsync(Path.Combine(FindRepositoryRoot(), "deploy", "bootstrap", "profiles", "bootstrap-workspace-example", "10-flow.yaml"));
        var flowName = "bootstrap-echo";
        using (var archive = ZipFile.Open(Path.Combine(profileDir, "pack.zip"), ZipArchiveMode.Create))
        {
            await WriteZipEntryAsync(archive, "pack.yaml", """
                apiVersion: agentstration.io/v1
                kind: Pack
                metadata: { name: fixture-pack, publisher: example, version: 2.4.0 }
                definition: { resources: [flow.yaml] }
                """);
            await WriteZipEntryAsync(archive, "flow.yaml", fixtureFlow);
        }
        await using var factory = PlanFactory(catalog);
        using var client = factory.CreateClient();
        var context = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        await using var services = factory.Services.CreateAsyncScope();
        var scopes = services.ServiceProvider.GetRequiredService<IRequestContextScopeFactory>();
        var identities = services.ServiceProvider.GetRequiredService<IIdentityStore>();
        var management = services.ServiceProvider.GetRequiredService<BootstrapProfileManagementService>();
        var second = new Workspace(Guid.NewGuid(), context.TenantId, "second-workspace", "Second workspace", WorkspaceStatus.Initializing, DateTimeOffset.UtcNow);
        using (scopes.PushSystem())
        {
            await identities.AddWorkspaceAsync(second, default);
            await services.ServiceProvider.GetRequiredService<IWorkspaceProvisioner>().ProvisionAsync(second, default);
        }

        var firstTarget = new BootstrapApplicationTarget(context.TenantId, context.WorkspaceId);
        var secondTarget = new BootstrapApplicationTarget(context.TenantId, second.Id);
        var profile = new BootstrapProfileSelection([profileName], firstTarget);
        var preview = await management.PreviewAsync(profile, context.PrincipalId, default);
        Assert.HasCount(0, await services.ServiceProvider.GetRequiredService<IBootstrapVersionHistory>().ListProfilesAsync(default));
        foreach (var target in new[] { firstTarget, secondTarget })
        {
            var selection = profile with { Target = target };
            preview = await management.PreviewAsync(selection, context.PrincipalId, default);
            var completed = await management.ApplyAsync(selection, preview.Digest, context.PrincipalId, default);
            Assert.AreEqual(BootstrapApplicationStatus.Succeeded, completed.Definition.Status, completed.Definition.Error);
        }

        var histories = services.ServiceProvider.GetRequiredService<IBootstrapVersionHistory>();
        var registered = (await histories.ListProfilesAsync(default)).Single();
        Assert.HasCount(1, await histories.ListVersionsAsync(registered.Id, default));
        var firstScope = ResourceScopeRef.Workspace(context.WorkspaceId);
        var secondScope = ResourceScopeRef.Workspace(second.Id);
        var firstRun = (await HistoryAsync(client, registered.Id, "1.0.0", firstScope)).Single();
        var secondRun = (await HistoryAsync(client, registered.Id, "1.0.0", secondScope)).Single();
        Assert.AreNotEqual(firstRun.Id, secondRun.Id);
        Assert.IsNull(await histories.GetAsync(secondScope, firstRun.Id, default));
        var installedFlow = firstRun.Resources.Single(row => row.Kind == FlowResourceKinds.Flow);
        Assert.AreEqual(BootstrapResourceDisposition.Create, installedFlow.Disposition);
        Assert.AreEqual("example/fixture-pack", installedFlow.Pack);
        Assert.IsTrue(installedFlow.ResourceAvailable);

        var flows = services.ServiceProvider.GetRequiredService<FlowService>();
        var flowId = new FlowId(flowName, new ResourceNamespace("example.fixture-pack"));
        var stored = await flows.GetExactAsync(firstScope, flowId, default);
        Assert.IsNotNull(stored);
        Assert.AreEqual(ResourceOriginKind.Pack, stored.Value.Governance.Origin.Kind);
        Assert.AreEqual("2.4.0", stored.Value.Governance.Origin.Version);
        Assert.AreEqual(firstRun.Id, stored.Value.Governance.AppliedBy?.ApplicationId);

        await File.AppendAllTextAsync(Path.Combine(profileDir, "10.yaml"), "\n# Same version, different bytes");
        await Assert.ThrowsAsync<DeclarativeBootstrapException>(() => management.PreviewAsync(profile, context.PrincipalId, default));
        var descriptor = Path.Combine(profileDir, "profile.yaml");
        var contents = await File.ReadAllTextAsync(descriptor);
        await File.WriteAllTextAsync(descriptor, contents.Replace("1.0.0", "1.1.0", StringComparison.Ordinal));
        preview = await management.PreviewAsync(profile, context.PrincipalId, default);
        var reapplied = await management.ApplyAsync(profile, preview.Digest, context.PrincipalId, default);
        Assert.AreEqual(BootstrapApplicationStatus.Succeeded, reapplied.Definition.Status);
        Assert.AreEqual(BootstrapResourceDisposition.Skip, (await HistoryAsync(client, registered.Id, "1.1.0", firstScope)).Single().Resources.Single().Disposition);
        Assert.AreEqual(firstRun.Id, (await flows.GetExactAsync(firstScope, flowId, default))?.Value.Governance.AppliedBy?.ApplicationId);
    }

    private static async Task WriteZipEntryAsync(ZipArchive archive, string path, string contents)
    {
        await using var writer = new StreamWriter(archive.CreateEntry(path).Open());
        await writer.WriteAsync(contents);
    }

    private static async Task<ScopedBootstrapApplication[]> HistoryAsync(HttpClient client, string profileId, string version, ResourceScopeRef scope) =>
        (await client.GetFromJsonAsync<ScopedBootstrapApplication[]>(
            $"/api/bootstrap/applied-profiles/{profileId}/versions/{version}/applications?scope={Uri.EscapeDataString(scope.Value)}"))!;

    [TestMethod]
    public async Task PlanRejectsUnknownFutureTargetsForgedWorkspaceOwnershipAndUnknownBindingProducers()
    {
        using var catalog = new PlanCatalog();
        await catalog.WriteAsync();
        await using var factory = PlanFactory(catalog);
        using var client = factory.CreateClient();
        var context = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        foreach (var applications in new IReadOnlyList<BootstrapProfileApplication>[]
        {
            [new("tenant", PlannedTarget: new("not-produced"))],
            [new("workspace", new(Guid.NewGuid(), context.WorkspaceId), FuturePlan()[0].Bindings)],
            [.. FuturePlan().Select(item => item.Profile == "workspace" ? item with { Bindings = [new("workspace", "origin", new("plan-origin"), TargetProfile: "unknown")] } : item)]
        })
        {
            using var response = await client.PostAsJsonAsync("/api/bootstrap/profiles/preview", new BootstrapProfilePreviewRequest([], Applications: applications));
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }
        await using var scope = factory.Services.CreateAsyncScope();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => scope.ServiceProvider.GetRequiredService<BootstrapProfileManagementService>()
            .PreviewAsync(new([], Applications: FuturePlan()), Guid.NewGuid(), default));
        Assert.IsNull(await scope.ServiceProvider.GetRequiredService<IIdentityStore>().FindTenantByNameAsync("plan-tenant", default));
    }

    [TestMethod]
    public async Task CatalogDriftInvalidatesTheWholePlanBeforeAnyTargetIsCreated()
    {
        using var catalog = new PlanCatalog();
        await catalog.WriteAsync();
        await using var factory = PlanFactory(catalog);
        using var client = factory.CreateClient();
        var applications = FuturePlan();
        using var previewResponse = await client.PostAsJsonAsync("/api/bootstrap/profiles/preview", new BootstrapProfilePreviewRequest([], Applications: applications));
        previewResponse.EnsureSuccessStatusCode();
        var preview = (await previewResponse.Content.ReadFromJsonAsync<BootstrapCompositionPreview>())!;
        await File.AppendAllTextAsync(System.IO.Path.Combine(catalog.Path, "tenant", "10.yaml"), "\n# Changed after preview\n");
        using var accepted = await client.PostAsJsonAsync("/api/bootstrap/applications", new ApplyBootstrapProfilesRequest([], preview.Digest, RequestId: Guid.NewGuid(), Applications: applications));
        accepted.EnsureSuccessStatusCode();
        var application = (await accepted.Content.ReadFromJsonAsync<BootstrapApplicationResource>())!;
        var done = await WaitForApplicationAsync(client, application.Metadata.Name);
        Assert.AreEqual(BootstrapApplicationStatus.Failed, done.Definition.Status);
        Assert.AreEqual("bootstrap_application_invalid", done.Definition.Error);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.IsNull(await scope.ServiceProvider.GetRequiredService<IIdentityStore>().FindTenantByNameAsync("plan-tenant", default));
    }

    [TestMethod]
    public async Task ProfilesAtTheSameLevelCanHaveDifferentTargetsAndRetainTheirChosenOrder()
    {
        using var catalog = new PlanCatalog();
        await catalog.WriteAsync();
        await catalog.ProfileAsync("tenant-second", "tenant", "apiVersion: agentstration.io/v1\nkind: Parameter\nmetadata: { name: plan-second }\ndefinition: { displayName: Plan second, valueType: integer, value: 9 }\n");
        await using var factory = PlanFactory(catalog);
        using var client = factory.CreateClient();
        var context = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        BootstrapProfileApplication[] applications = [new("tenant-second", new(context.TenantId)), new("tenant", PlannedTarget: new("plan-tenant")), new("instance")];
        using var response = await client.PostAsJsonAsync("/api/bootstrap/profiles/preview", new BootstrapProfilePreviewRequest([], Applications: applications));
        response.EnsureSuccessStatusCode();
        var preview = (await response.Content.ReadFromJsonAsync<BootstrapCompositionPreview>())!;
        CollectionAssert.AreEqual(new[] { "instance", "tenant-second", "tenant" }, preview.Profiles.Select(item => item.Name).ToArray());
        using var accepted = await client.PostAsJsonAsync("/api/bootstrap/applications", new ApplyBootstrapProfilesRequest([], preview.Digest, RequestId: Guid.NewGuid(), Applications: applications));
        accepted.EnsureSuccessStatusCode();
        var done = await WaitForApplicationAsync(client, (await accepted.Content.ReadFromJsonAsync<BootstrapApplicationResource>())!.Metadata.Name);
        Assert.AreEqual(BootstrapApplicationStatus.Succeeded, done.Definition.Status, done.Definition.Error);
        await using var scope = factory.Services.CreateAsyncScope();
        using var system = scope.ServiceProvider.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var parameters = scope.ServiceProvider.GetRequiredService<ParameterManagementService>();
        var tenant = (await scope.ServiceProvider.GetRequiredService<IIdentityStore>().FindTenantByNameAsync("plan-tenant", default))!;
        Assert.IsNotNull(await parameters.GetExactAsync(ResourceScopeRef.Tenant(context.TenantId), "plan-second", default));
        Assert.IsNull(await parameters.GetExactAsync(ResourceScopeRef.Tenant(tenant.Id), "plan-second", default));
    }

    [TestMethod]
    public async Task CancelledPlanKeepsEarlierTargetsAndDoesNotRunWorkspaceProfiles()
    {
        using var catalog = new PlanCatalog();
        await catalog.WriteAsync();
        var gate = new LoadTestGate { ResourceName = "plan-origin" };
        await using var factory = PlanFactory(catalog, gate);
        using var client = factory.CreateClient();
        var applications = FuturePlan();
        using var response = await client.PostAsJsonAsync("/api/bootstrap/profiles/preview", new BootstrapProfilePreviewRequest([], Applications: applications));
        response.EnsureSuccessStatusCode();
        var preview = (await response.Content.ReadFromJsonAsync<BootstrapCompositionPreview>())!;
        using var accepted = await client.PostAsJsonAsync("/api/bootstrap/applications", new ApplyBootstrapProfilesRequest([], preview.Digest, RequestId: Guid.NewGuid(), Applications: applications));
        accepted.EnsureSuccessStatusCode();
        var application = (await accepted.Content.ReadFromJsonAsync<BootstrapApplicationResource>())!;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            using var cancelled = await client.PostAsync($"/api/bootstrap/applications/{application.Metadata.Name}/cancel", null);
            cancelled.EnsureSuccessStatusCode();
        }
        finally { gate.Release.TrySetResult(); }
        var done = await WaitForApplicationAsync(client, application.Metadata.Name);
        Assert.AreEqual(BootstrapApplicationStatus.Cancelled, done.Definition.Status);
        Assert.IsTrue(done.Definition.Resources.Any(item => item.Profile == "instance"));
        Assert.IsFalse(done.Definition.Resources.Any(item => item.Profile == "workspace"));
    }

    [TestMethod]
    public async Task DeferredValidationFailureStopsThePlanAndPreservesItsEarlierResources()
    {
        using var catalog = new PlanCatalog();
        await catalog.WriteAsync();
        var file = System.IO.Path.Combine(catalog.Path, "workspace", "10.yaml");
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("flowKind: workflow", "flowKind: unknown-kind", StringComparison.Ordinal));
        await using var factory = PlanFactory(catalog);
        using var client = factory.CreateClient();
        var applications = FuturePlan();
        using var response = await client.PostAsJsonAsync("/api/bootstrap/profiles/preview", new BootstrapProfilePreviewRequest([], Applications: applications));
        response.EnsureSuccessStatusCode();
        var preview = (await response.Content.ReadFromJsonAsync<BootstrapCompositionPreview>())!;
        Assert.IsTrue(preview.CanApply);
        Assert.IsTrue(preview.Applications.Single(item => item.Profile == "workspace").ValidationDeferred);
        using var accepted = await client.PostAsJsonAsync("/api/bootstrap/applications", new ApplyBootstrapProfilesRequest([], preview.Digest, RequestId: Guid.NewGuid(), Applications: applications));
        accepted.EnsureSuccessStatusCode();
        var done = await WaitForApplicationAsync(client, (await accepted.Content.ReadFromJsonAsync<BootstrapApplicationResource>())!.Metadata.Name);
        Assert.AreEqual(BootstrapApplicationStatus.PartiallyApplied, done.Definition.Status);
        Assert.IsTrue(done.Definition.Resources.Any(item => item.Kind == "Tenant" && item.Disposition == BootstrapResourceDisposition.Create));
        Assert.IsTrue(done.Definition.Resources.Any(item => item.Kind == "Flow" && item.Disposition == BootstrapResourceDisposition.Failed));
        Assert.IsFalse(done.Definition.Resources.Any(item => item.Kind == "Entry"));
    }

    private static WebApplicationFactory<Program> PlanFactory(PlanCatalog catalog, LoadTestGate? gate = null) =>
        LoadTestFactory(gate).WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Agentstration:Bootstrap:Path", catalog.Path);
            builder.ConfigureServices(services => services.AddScoped<IBootstrapResourceHandler>(_ => new PlanProbeHandler(catalog)));
        });

    private sealed class PlanProbeHandler(PlanCatalog catalog) : IBootstrapResourceHandler
    {
        public string Kind => "PlanProbe";
        public BootstrapProfileScope Scope => BootstrapProfileScope.Workspace;
        public Task<BootstrapResourcePlanResult> PlanAsync(BootstrapResourceDocument resource, BootstrapResourceOperationContext operation, BootstrapPlanningContext planning, CancellationToken cancellationToken) => Task.FromResult(new BootstrapResourcePlanResult(BootstrapResourceDisposition.Create));
        public Task<BootstrapResourceApplyResult> ApplyAsync(BootstrapResourceDocument resource, BootstrapResourceOperationContext operation, CancellationToken cancellationToken)
        {
            Assert.IsTrue(operation.WaitForDependencies, "Manual plans must use the existing model dependency barrier.");
            catalog.Reference = resource.Definition.GetProperty("reference").Clone();
            return Task.FromResult(BootstrapResourceApplyResult.Created);
        }
    }

    private static BootstrapProfileApplication[] FuturePlan() =>
    [
        new("workspace", Bindings: [new("workspace", "origin", new("plan-origin"), TargetProfile: "tenant")], PlannedTarget: new("plan-tenant", "plan-workspace")),
        new("tenant", PlannedTarget: new("plan-tenant")),
        new("instance")
    ];

    private sealed class PlanCatalog : IDisposable
    {
        public JsonElement? Reference { get; set; }
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agentstration-plan-" + Guid.NewGuid().ToString("N"));
        public async Task ProfileAsync(string name, string scope, string content, string bindings = "")
        {
            var directory = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "profile.yaml"), $"apiVersion: agentstration.io/v1\nkind: BootstrapProfile\nmetadata: {{ name: {name} }}\ndefinition:\n  displayName: {name}\n  version: 1.0.0\n  targetScope: {scope}\n{bindings}");
            await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "10.yaml"), content);
        }
        public async Task WriteAsync()
        {
            await ProfileAsync("instance", "instance", "apiVersion: agentstration.io/v1\nkind: Tenant\nmetadata: { name: plan-tenant }\ndefinition: { displayName: Plan Tenant }\n");
            await ProfileAsync("tenant", "tenant", "apiVersion: agentstration.io/v1\nkind: Workspace\nmetadata: { name: plan-workspace }\ndefinition: { displayName: Plan Workspace }\n---\napiVersion: agentstration.io/v1\nkind: Parameter\nmetadata: { name: plan-origin }\ndefinition: { displayName: Plan origin, valueType: integer, value: 42 }\n");
            var root = System.IO.Path.Combine(FindRepositoryRoot(), "deploy", "bootstrap", "profiles", "bootstrap-workspace-example");
            await ProfileAsync("workspace", "workspace", await File.ReadAllTextAsync(System.IO.Path.Combine(root, "10-flow.yaml")), "  bindings:\n    - { name: origin, targetKind: parameter, required: true }\n");
            var directory = System.IO.Path.Combine(Path, "workspace");
            File.Copy(System.IO.Path.Combine(root, "20-entry.yaml"), System.IO.Path.Combine(directory, "20-entry.yaml"));
            await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "30-parameter.yaml"), "apiVersion: agentstration.io/v1\nkind: PlanProbe\nmetadata: { name: plan-consumer }\ndefinition:\n  reference: { binding: origin }\n");
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
