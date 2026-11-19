using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Api.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.ModelProviders;
using Agentstration.Models;
using Agentstration.Models.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ModelProviderDiscoveryLifecycleTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task SlowProviderDoesNotBlockCommandsOrReadsAndRefreshesAreCoalesced()
    {
        var adapter = new ControlledDiscovery { Block = true };
        await using var factory = DiscoveryFactory(adapter);
        using var client = factory.CreateClient();
        SelectConsoleContext(client, ResourceScopeRef.Instance);
        using var before = await client.GetAsync("/api/modelproviders/ollama-local");
        var etag = before.Headers.ETag;
        for (var index = 0; index < 3; index++)
        {
            using var queued = await client.PostAsync("/api/modelproviders/ollama-local/models/refresh", null);
            Assert.AreEqual(HttpStatusCode.Accepted, queued.StatusCode);
            Assert.AreEqual(0, adapter.HealthCalls);
        }
        var processing = ProcessDiscoveryAsync(factory, "ollama-local");
        await adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await ProcessDiscoveryAsync(factory, "ollama-local");
        Assert.AreEqual(1, adapter.HealthCalls);
        var pending = await client.GetFromJsonAsync<ModelProviderStatusResponse>("/api/modelproviders/ollama-local/status")
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("running", pending!.Discovery!.State);
        Assert.IsNull(pending.Discovery.LastSuccessAt);
        var providers = await client.GetFromJsonAsync<ValueResponse<ModelProviderResponse>>("/api/modelproviders")
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsNotNull(providers);
        using var models = await client.GetAsync("/api/modelproviders/ollama-local/models").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(HttpStatusCode.OK, models.StatusCode);
        adapter.Release.TrySetResult();
        await processing;
        var completed = await client.GetFromJsonAsync<ModelProviderStatusResponse>("/api/modelproviders/ollama-local/status");
        Assert.AreEqual("succeeded", completed!.Discovery!.State);
        Assert.AreEqual("available", completed.Status);
        Assert.AreEqual(0, completed.Discovery.Result!.Total);
        Assert.IsNotNull(completed.Discovery.LastSuccessAt);
        using var after = await client.GetAsync("/api/modelproviders/ollama-local");
        Assert.AreEqual(etag, after.Headers.ETag, "Observations must not modify the governed provider ETag.");
    }

    [TestMethod]
    public async Task FailedDiscoveryRetriesAutomaticallyAndRetainsCatalogAndLastSuccess()
    {
        var clock = new DiscoveryClock();
        var adapter = new ControlledDiscovery { Models = [Model("installed")] };
        await using var factory = DiscoveryFactory(adapter).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock))));
        using var client = factory.CreateClient();
        SelectConsoleContext(client, ResourceScopeRef.Instance);
        await ProcessDiscoveryAsync(factory, "ollama-local");
        var first = await client.GetFromJsonAsync<ModelProviderStatusResponse>("/api/modelproviders/ollama-local/status");
        adapter.FailCatalog = true;
        clock.Advance(ModelProviderDiscoveryState.RefreshInterval);
        await ProcessDiscoveryAsync(factory, "ollama-local");
        var failed = await client.GetFromJsonAsync<ModelProviderStatusResponse>("/api/modelproviders/ollama-local/status");
        Assert.AreEqual("failed", failed!.Discovery!.State);
        Assert.AreEqual("available", failed.Status, "A catalog failure must not overwrite an observed healthy provider.");
        Assert.AreEqual(first!.Discovery!.LastSuccessAt, failed.Discovery.LastSuccessAt);
        Assert.AreEqual("discovery_failed", failed.Discovery.ErrorCode);
        Assert.AreEqual(clock.GetUtcNow().AddSeconds(5), failed.Discovery.NextAttemptAt);
        var catalog = await client.GetFromJsonAsync<ValueResponse<AvailableModelResponse>>("/api/modelproviders/ollama-local/models");
        Assert.AreEqual("installed", catalog!.Value.Single().Name);
        await ProcessDiscoveryAsync(factory, "ollama-local");
        Assert.AreEqual(2, adapter.HealthCalls, "Backoff must prevent immediate background retries.");
        clock.Advance(TimeSpan.FromSeconds(5));
        adapter.FailCatalog = false;
        adapter.Models = [Model("installed"), Model("late-model")];
        await factory.Services.GetRequiredService<ModelProviderDiscoveryLifecycle>().ProcessDueAsync(default);
        var recovered = await client.GetFromJsonAsync<ModelProviderStatusResponse>("/api/modelproviders/ollama-local/status");
        Assert.AreEqual("succeeded", recovered!.Discovery!.State);
        Assert.IsNull(recovered.Discovery.ErrorCode);
        Assert.AreEqual(2, recovered.Discovery.Result!.Total);
    }

    [TestMethod]
    public async Task InterruptedDiscoveryIsRecoveredFromItsPersistedLease()
    {
        var clock = new DiscoveryClock();
        var adapter = new ControlledDiscovery { Block = true };
        await using var factory = DiscoveryFactory(adapter).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock))));
        using var client = factory.CreateClient();
        using var system = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var provider = await factory.Services.GetRequiredService<ModelProviderManagementService>()
            .GetExactAsync(ResourceScopeRef.Instance, ResourceNamespace.Default, "ollama-local", default);
        using var stop = new CancellationTokenSource();
        var interrupted = factory.Services.GetRequiredService<ModelProviderDiscoveryLifecycle>().ProcessAsync(provider!.Value, stop.Token);
        await adapter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => interrupted);
        var store = factory.Services.GetRequiredService<Agentstration.ResourceManagement.IResourceStore>();
        // A newly constructed state reader has no process-local queue or completion to recover.
        var restored = new ModelProviderDiscoveryState(store, clock);
        Assert.AreEqual("running", (await restored.ReadAsync(provider.Value, default)).State);
        adapter.Block = false;
        clock.Advance(TimeSpan.FromMinutes(3));
        await ProcessDiscoveryAsync(factory, "ollama-local");
        Assert.AreEqual("succeeded", (await restored.ReadAsync(provider.Value, default)).State);
    }

    [TestMethod]
    public async Task CreatedAndUpdatedProvidersQueueDiscoveryWithoutCallingTheProvider()
    {
        var adapter = new ControlledDiscovery();
        await using var factory = DiscoveryFactory(adapter);
        using var client = factory.CreateClient();
        SelectConsoleContext(client, ResourceScopeRef.Instance);
        var template = await client.GetFromJsonAsync<ModelProviderResource>("/api/modelproviders/ollama-local");
        using var created = await client.PostAsJsonAsync("/api/modelproviders", new CreateModelProviderRequest(
            "async-provider", template!.Definition, ScopeRef: ResourceScopeRef.Instance));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var queued = await client.GetFromJsonAsync<ModelProviderStatusResponse>("/api/modelproviders/async-provider/status");
        Assert.AreEqual("queued", queued!.Discovery!.State);
        Assert.AreEqual(0, adapter.HealthCalls);
        await ProcessDiscoveryAsync(factory, "async-provider");
        using var current = await client.GetAsync("/api/modelproviders/async-provider");
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/modelproviders/async-provider")
        {
            Content = JsonContent.Create(new PutModelProviderRequest(template.Definition with { DisplayName = "Updated" }))
        };
        request.Headers.IfMatch.Add(current.Headers.ETag!);
        using var updated = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode);
        queued = await client.GetFromJsonAsync<ModelProviderStatusResponse>("/api/modelproviders/async-provider/status");
        Assert.AreEqual("queued", queued!.Discovery!.State);
        Assert.AreEqual("unknown", queued.Status);
        Assert.IsNull(queued.Discovery.LastSuccessAt);
        Assert.AreEqual(1, adapter.HealthCalls);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public async Task StagedBootstrapRequiresTheExactModelBeforeCreatingAProfile(bool modelInstalled, bool failedBeforeStartup)
    {
        var adapter = new ControlledDiscovery { Models = [Model(modelInstalled ? "required-model" : "another-model")] };
        await using var factory = DiscoveryFactory(adapter).WithWebHostBuilder(builder =>
            builder.UseSetting("Agentstration:Bootstrap:ModelDependencyTimeout", "00:00:01"));
        using var client = factory.CreateClient();
        using var system = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        if (failedBeforeStartup)
        {
            adapter.FailCatalog = true;
            await ProcessDiscoveryAsync(factory, "ollama-local");
            adapter.FailCatalog = false;
        }
        using var execution = factory.Services.CreateScope();
        var handler = execution.ServiceProvider.GetServices<IBootstrapResourceHandler>()
            .Single(value => value.Kind == ModelResourceKinds.ModelProfile);
        var document = new BootstrapResourceDocument
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ModelResourceKinds.ModelProfile,
            Metadata = new() { Name = "required-profile" },
            Definition = JsonSerializer.SerializeToElement(new ModelProfileProperties
            {
                DisplayName = "Required profile",
                Provider = new("ollama-local", ResourceScopeRef.Instance),
                Model = new() { Name = "required-model" }
            }, JsonSerializerOptions.Web)
        };
        var operation = new BootstrapResourceOperationContext("model-test", ".", BootstrapProfileScope.Instance, WaitForDependencies: true);
        if (modelInstalled)
            Assert.AreEqual(BootstrapResourceApplyResult.Created, await handler.ApplyAsync(document, operation, default));
        else
        {
            var error = await Assert.ThrowsAsync<DeclarativeBootstrapException>(() => handler.ApplyAsync(document, operation, default));
            StringAssert.Contains(error.Message, "model_dependency_timeout");
            StringAssert.Contains(error.Message, "required-model");
            StringAssert.Contains(error.Message, "ollama-local");
        }
        var saved = await factory.Services.GetRequiredService<ModelProfileManagementService>()
            .GetExactAsync(ResourceScopeRef.Instance, ResourceNamespace.Default, "required-profile", default);
        Assert.AreEqual(modelInstalled, saved is not null);
    }

    [TestMethod]
    public async Task AdministrationRemainsAccessibleDuringBootstrapWhileExecutionStaysFenced()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();
        SelectConsoleContext(client, ResourceScopeRef.Instance);
        var startup = factory.Services.GetRequiredService<BootstrapStartupState>();
        startup.Begin();
        startup.EnableAdministration();
        using var observed = await client.GetAsync("/api/modelproviders/ollama-local/status");
        Assert.AreEqual(HttpStatusCode.OK, observed.StatusCode);
        using var ready = await client.GetAsync("/health/ready");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        using var execution = await client.GetAsync("/api/tools");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, execution.StatusCode);
        startup.Fail("model_dependency_timeout");
        using var afterFailure = await client.GetAsync("/api/modelproviders/ollama-local/status");
        Assert.AreEqual(HttpStatusCode.OK, afterFailure.StatusCode);
        Assert.IsFalse(startup.IsReady);
    }

    private static DiscoveredModel Model(string name) => new(name, name, "available", new());

    private sealed class DiscoveryClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class ControlledDiscovery : IModelProviderDiscovery
    {
        public string ProviderType => "aep";
        public bool Block { get; set; }
        public bool FailCatalog { get; set; }
        public int HealthCalls { get; private set; }
        public IReadOnlyList<DiscoveredModel> Models { get; set; } = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<ModelProviderHealth> GetHealthAsync(ModelProviderConfiguration provider, CancellationToken cancellationToken = default)
        {
            HealthCalls++;
            Entered.TrySetResult();
            if (Block) await Release.Task.WaitAsync(cancellationToken);
            return new("available");
        }
        public ValueTask<IReadOnlyList<DiscoveredModel>> ListModelsAsync(ModelProviderConfiguration provider, CancellationToken cancellationToken = default) =>
            FailCatalog ? ValueTask.FromException<IReadOnlyList<DiscoveredModel>>(new InvalidOperationException("upstream failure"))
                : ValueTask.FromResult(Models);
    }
}
