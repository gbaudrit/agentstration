using System.Net;
using System.Net.Http.Json;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Bootstrap;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Web.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agentstration.Management.Tests;

public sealed partial class DeclarativeBootstrapTests
{
    [TestMethod]
    public async Task LoadTestCatalogHas512OfflineParametersAndIsNeverInitiallySelected()
    {
        await using var factory = LoadTestFactory();
        using var client = factory.CreateClient();
        var view = await client.GetFromJsonAsync<BootstrapManagementView>("/api/bootstrap/profiles");
        Assert.IsNotNull(view);
        var profile = view.Catalog.Profiles.Single(value => value.Name == "load-test");
        Assert.IsTrue(profile.Valid, profile.Error);
        Assert.AreEqual(BootstrapProfileScope.Tenant, profile.Scope);
        Assert.AreEqual(512, profile.ResourceCount);
        Assert.IsFalse(view.Catalog.InitialProfiles.Contains("load-test", StringComparer.Ordinal));
        foreach (var host in new[] { "Agentstration.Web", "Agentstration.AppHost" })
            Assert.DoesNotContain("load-test", await File.ReadAllTextAsync(
                Path.Combine(FindRepositoryRoot(), "src", host, "Properties", "launchSettings.json")), StringComparison.Ordinal);
        var loaded = (await factory.Services.GetRequiredService<BootstrapProfileCatalog>().LoadAsync(["load-test"], default)).Single();
        Assert.IsTrue(loaded.Resources.All(value => value.Resource.Kind == "Parameter"));
        Assert.AreEqual(512, loaded.Resources.Select(value => value.Resource.Metadata.Name).Distinct().Count());
    }

    [TestMethod]
    [Timeout(180_000)]
    public async Task AcceptedLoadTestSurvivesClientDisconnectAndReportsMonotonicDurableProgress()
    {
        var gate = new LoadTestGate();
        await using var factory = LoadTestFactory(gate);
        using var client = factory.CreateClient();
        var context = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        var target = new BootstrapApplicationTarget(context.TenantId);
        using var previewResponse = await client.PostAsJsonAsync("/api/bootstrap/profiles/preview", new BootstrapProfilePreviewRequest(["load-test"], target));
        previewResponse.EnsureSuccessStatusCode();
        var preview = (await previewResponse.Content.ReadFromJsonAsync<BootstrapCompositionPreview>())!;
        Assert.AreEqual(512, preview.Resources.Count);
        var request = new ApplyBootstrapProfilesRequest(["load-test"], preview.Digest, target, RequestId: Guid.NewGuid());
        string id;
        using (var initiatingClient = factory.CreateClient())
        {
            initiatingClient.Timeout = TimeSpan.FromSeconds(1);
            using var accepted = await initiatingClient.PostAsJsonAsync("/api/bootstrap/applications", request);
            Assert.AreEqual(HttpStatusCode.Accepted, accepted.StatusCode);
            var application = (await accepted.Content.ReadFromJsonAsync<BootstrapApplicationResource>())!;
            id = application.Metadata.Name;
            Assert.AreEqual($"/api/bootstrap/applications/{id}", accepted.Headers.Location?.OriginalString);
        }
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            // The operation stays alive beyond the ordinary 15-second Console request budget.
            await Task.Delay(TimeSpan.FromSeconds(16));
            using var pollClient = factory.CreateClient();
            pollClient.Timeout = TimeSpan.FromSeconds(1);
            var running = (await pollClient.GetFromJsonAsync<BootstrapApplicationResource>($"/api/bootstrap/applications/{id}"))!;
            Assert.AreEqual(BootstrapApplicationStatus.Running, running.Definition.Status);
            Assert.AreEqual(512, running.Definition.TotalResources);
            Assert.HasCount(1, running.Definition.Resources);
            Assert.AreEqual("bootstrap-load-test-002", running.Definition.CurrentResource?.Name);
            using var duplicate = await pollClient.PostAsJsonAsync("/api/bootstrap/applications", request);
            Assert.AreEqual(id, (await duplicate.Content.ReadFromJsonAsync<BootstrapApplicationResource>())?.Metadata.Name);
            var anotherRequest = request with { RequestId = Guid.NewGuid() };
            using var anotherClick = await pollClient.PostAsJsonAsync("/api/bootstrap/applications", anotherRequest);
            Assert.AreEqual(id, (await anotherClick.Content.ReadFromJsonAsync<BootstrapApplicationResource>())?.Metadata.Name);
            using var different = await pollClient.PostAsJsonAsync("/api/bootstrap/applications", request with { ExpectedDigest = "different" });
            Assert.AreEqual(HttpStatusCode.BadRequest, different.StatusCode);
            gate.Release.TrySetResult();
            var completed = await WaitForApplicationAsync(client, id);
            Assert.AreEqual(BootstrapApplicationStatus.Succeeded, completed.Definition.Status);
            Assert.HasCount(512, completed.Definition.Resources);
            Assert.IsTrue(completed.Definition.Resources.All(value => value.Disposition == BootstrapResourceDisposition.Create));
            Assert.IsNull(completed.Definition.CurrentResource);
            using var aliasReplay = await client.PostAsJsonAsync("/api/bootstrap/applications", anotherRequest);
            Assert.AreEqual(id, (await aliasReplay.Content.ReadFromJsonAsync<BootstrapApplicationResource>())?.Metadata.Name);
            using var replay = await client.PostAsJsonAsync("/api/bootstrap/applications", request);
            Assert.AreEqual(id, (await replay.Content.ReadFromJsonAsync<BootstrapApplicationResource>())?.Metadata.Name);
            await using var scope = factory.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<BootstrapProfileManagementService>();
            var repeat = await service.StartAsync(request with { RequestId = Guid.NewGuid() }, context.PrincipalId, default);
            repeat = await WaitForApplicationAsync(client, repeat.Metadata.Name);
            Assert.HasCount(512, repeat.Definition.Resources);
            Assert.IsTrue(repeat.Definition.Resources.All(value => value.Disposition == BootstrapResourceDisposition.Skip));
            using var systemScope = scope.ServiceProvider.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
            var parameters = scope.ServiceProvider.GetRequiredService<ParameterManagementService>();
            Assert.IsNotNull(await parameters.GetExactAsync(ResourceScopeRef.Tenant(context.TenantId), "bootstrap-load-test-001", default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => parameters.GetExactAsync(
                ResourceScopeRef.Tenant(Guid.NewGuid()), "bootstrap-load-test-001", default));
        }
        finally { gate.Release.TrySetResult(); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellationAndResourceFailureRetainCompletedOutcomes(bool fail)
    {
        var gate = new LoadTestGate { Fail = fail };
        await using var factory = LoadTestFactory(gate);
        using var client = factory.CreateClient();
        var context = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<BootstrapProfileManagementService>();
        var selection = new BootstrapProfileSelection(["load-test"], new(context.TenantId));
        var preview = await service.PreviewAsync(selection, context.PrincipalId, default);
        var application = await service.StartAsync(new(selection.Profiles, preview.Digest, selection.Target), context.PrincipalId, default);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (!fail)
            {
                using var cancelled = await client.PostAsync($"/api/bootstrap/applications/{application.Metadata.Name}/cancel", null);
                Assert.AreEqual(HttpStatusCode.Accepted, cancelled.StatusCode);
            }
            gate.Release.TrySetResult();
            var terminal = await WaitForApplicationAsync(client, application.Metadata.Name);
            Assert.AreEqual(fail ? BootstrapApplicationStatus.PartiallyApplied : BootstrapApplicationStatus.Cancelled, terminal.Definition.Status);
            Assert.IsTrue(terminal.Definition.Resources.Count >= 1);
            Assert.IsNotNull(terminal.Definition.CompletedAt);
            Assert.IsNull(terminal.Definition.CurrentResource);
            Assert.AreEqual(fail ? BootstrapResourceDisposition.Failed : BootstrapResourceDisposition.Create, terminal.Definition.Resources[^1].Disposition);
        }
        finally { gate.Release.TrySetResult(); }
    }

    [TestMethod]
    public async Task ChangedDigestFailsBeforeMutationAndUnauthorizedActorsCannotStartOrObserve()
    {
        await using var factory = LoadTestFactory();
        using var client = factory.CreateClient();
        var context = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<BootstrapProfileManagementService>();
        using var invalidRequest = await client.PostAsJsonAsync("/api/bootstrap/applications", new { profiles = (string[]?)null, expectedDigest = "digest" });
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidRequest.StatusCode);
        var request = new ApplyBootstrapProfilesRequest(["load-test"], "stale", new(context.TenantId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.StartAsync(request, Guid.NewGuid(), default));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.GetApplicationAsync("unknown", Guid.NewGuid(), default));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => service.CancelAsync("unknown", Guid.NewGuid(), default));
        await Assert.ThrowsAsync<DeclarativeBootstrapException>(() => service.StartAsync(request with { Target = new(Guid.NewGuid(), context.WorkspaceId) }, context.PrincipalId, default));
        var application = await service.StartAsync(request, context.PrincipalId, default);
        var completed = await WaitForApplicationAsync(client, application.Metadata.Name);
        Assert.AreEqual(BootstrapApplicationStatus.Failed, completed.Definition.Status);
        Assert.HasCount(0, completed.Definition.Resources);
    }

    [TestMethod]
    public async Task InvalidResourceDetailsAreNotPersistedInApplicationHistory()
    {
        await using var factory = LoadTestFactory(new LoadTestGate { FailPlanning = true });
        using var client = factory.CreateClient();
        var context = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<BootstrapProfileManagementService>();
        var selection = new BootstrapProfileSelection(["load-test"], new(context.TenantId));
        var preview = await service.PreviewAsync(selection, context.PrincipalId, default);
        Assert.IsFalse(preview.CanApply);
        var application = await service.StartAsync(new(selection.Profiles, preview.Digest, selection.Target), context.PrincipalId, default);
        application = await WaitForApplicationAsync(client, application.Metadata.Name);
        Assert.AreEqual(BootstrapApplicationStatus.Failed, application.Definition.Status);
        Assert.AreEqual("bootstrap_application_invalid", application.Definition.Error);
        Assert.DoesNotContain("synthetic-sensitive-content", System.Text.Json.JsonSerializer.Serialize(application));
    }

    [TestMethod]
    public async Task NewProcessRecoversQueuedApplicationAsInterruptedWithoutReexecutingIt()
    {
        await using var factory = LoadTestFactory(disableWorker: true);
        using var client = factory.CreateClient();
        var context = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        await using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<BootstrapProfileManagementService>();
        var request = new ApplyBootstrapProfilesRequest(["load-test"], "digest", new(context.TenantId), RequestId: Guid.NewGuid());
        var application = await service.StartAsync(request, context.PrincipalId, default);
        var restarted = ActivatorUtilities.CreateInstance<BootstrapProfileManagementService>(scope.ServiceProvider, new BootstrapApplicationLock());
        var recovered = await restarted.GetApplicationAsync(application.Metadata.Name, context.PrincipalId, default);
        Assert.AreEqual(BootstrapApplicationStatus.Interrupted, recovered?.Definition.Status);
        Assert.IsNotNull(recovered?.Definition.CompletedAt);
        var same = await restarted.StartAsync(request, context.PrincipalId, default);
        Assert.AreEqual(application.Metadata.Name, same.Metadata.Name);
        Assert.AreEqual(BootstrapApplicationStatus.Interrupted, same.Definition.Status);
        Assert.IsFalse(await restarted.ProcessNextAsync(default));
    }

    private static WebApplicationFactory<Program> LoadTestFactory(LoadTestGate? gate = null, bool disableWorker = false) =>
        new BootstrapApiOnlyWebApplicationFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Agentstration:Authentication:Mode", "Development");
            builder.UseSetting("Agentstration:Authentication:DevelopmentGrantPlatformAdministrator", "true");
            builder.UseSetting("Agentstration:Bootstrap:Path", Path.Combine(FindRepositoryRoot(), "deploy", "bootstrap", "profiles"));
            builder.UseSetting("Agentstration:Bootstrap:InitialBootstrapEnabled", "false");
            builder.ConfigureServices(services =>
            {
                if (gate is not null)
                {
                    foreach (var descriptor in services.Where(value => value.ServiceType == typeof(IBootstrapResourceHandler)
                                 && value.ImplementationType == typeof(ParameterBootstrapResourceHandler)).ToArray()) services.Remove(descriptor);
                    services.AddScoped<IBootstrapResourceHandler>(provider => new GatedParameterHandler(
                        ActivatorUtilities.CreateInstance<ParameterBootstrapResourceHandler>(provider), gate));
                }
                if (disableWorker)
                    foreach (var descriptor in services.Where(value => value.ServiceType == typeof(IHostedService)
                                 && value.ImplementationType == typeof(BootstrapApplicationWorker)).ToArray()) services.Remove(descriptor);
            });
        });

    private static async Task<BootstrapApplicationResource> WaitForApplicationAsync(HttpClient client, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var processed = 0;
        while (true)
        {
            var application = (await client.GetFromJsonAsync<BootstrapApplicationResource>($"/api/bootstrap/applications/{id}", timeout.Token))!;
            Assert.IsTrue(application.Definition.Resources.Count >= processed, "Persisted progress must never go backwards.");
            processed = application.Definition.Resources.Count;
            if (application.Definition.Status != BootstrapApplicationStatus.Running) return application;
            await Task.Delay(50, timeout.Token);
        }
    }

    private sealed class LoadTestGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Fail { get; init; }
        public bool FailPlanning { get; init; }
    }

    private sealed class GatedParameterHandler(ParameterBootstrapResourceHandler inner, LoadTestGate gate) : IBootstrapResourceHandler
    {
        public string Kind => inner.Kind;
        public BootstrapProfileScope Scope => inner.Scope;
        public Task<BootstrapResourcePlanResult> PlanAsync(BootstrapResourceDocument resource, BootstrapResourceOperationContext operation,
            BootstrapPlanningContext planning, CancellationToken cancellationToken)
        {
            if (gate.FailPlanning) throw new InvalidOperationException("synthetic-sensitive-content");
            return inner.PlanAsync(resource, operation, planning, cancellationToken);
        }
        public async Task<BootstrapResourceApplyResult> ApplyAsync(BootstrapResourceDocument resource, BootstrapResourceOperationContext operation,
            CancellationToken cancellationToken)
        {
            if (resource.Metadata.Name == "bootstrap-load-test-002")
            {
                gate.Entered.TrySetResult();
                await gate.Release.Task.WaitAsync(cancellationToken);
                if (gate.Fail) throw new InvalidOperationException("Synthetic load-test failure.");
            }
            return await inner.ApplyAsync(resource, operation, cancellationToken);
        }
    }
}
