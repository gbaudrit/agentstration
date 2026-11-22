using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    }

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
            await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "profile.yaml"), $"apiVersion: agentstration.io/v1\nkind: BootstrapProfile\nmetadata: {{ name: {name} }}\ndefinition:\n  displayName: {name}\n  targetScope: {scope}\n{bindings}");
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
