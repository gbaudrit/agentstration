using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.ConnectedServices;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentstration.Management.Tests;

public sealed partial class DataSourceDatasetTests
{
    [TestMethod]
    public async Task RawSampleCallsOnlyTheOperationAndReplaysTheSameInputWithoutProductionHistory()
    {
        var operation = new SampleExecutor();
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<ISourceOperationSampleExecutor>(operation))));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var owner = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var source = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(Source("sample-only", fixture,
            JsonSerializer.SerializeToElement(new { dataset = "raw-input" })), default);
        using var client = factory.CreateClient();
        SelectConsoleContext(client, Owner);
        using var accepted = await client.PostAsJsonAsync("/api/source-samples", new AcquireSourceSampleRequest(source.Value.Definition.Dataset!.Acquisition));
        Assert.AreEqual(HttpStatusCode.Accepted, accepted.StatusCode, await accepted.Content.ReadAsStringAsync());
        var sample = (await accepted.Content.ReadFromJsonAsync<SourceSample>())!;
        var samples = factory.Services.GetRequiredService<SourceSampleService>();
        await samples.ProcessNextAsync(default);
        var raw = await samples.GetAsync(sample.Id, default);
        Assert.AreEqual(SourceSampleState.Ready, raw.State);
        Assert.AreEqual("raw-input", raw.Value!.Value.GetProperty("dataset").GetString());
        Assert.AreEqual(1, operation.Calls);
        var replayRequest = new ReplaySourceSampleRequest(ResourceNamespace.Default, [new() { Function = new("json-select-builtin", "1.0.0", Owner), Parameters = JsonSerializer.SerializeToElement(new { path = "/dataset" }) }]);
        using var replayResponse = await client.PostAsJsonAsync($"/api/source-samples/{sample.Id}/preview", replayRequest);
        Assert.AreEqual(HttpStatusCode.OK, replayResponse.StatusCode, await replayResponse.Content.ReadAsStringAsync());
        var replay = (await replayResponse.Content.ReadFromJsonAsync<SourceSequencePreview>())!;
        Assert.IsNull(replay.ErrorCode);
        Assert.AreEqual("raw-input", replay.Values[1].GetString());
        var again = await samples.ReplayAsync(sample.Id, replayRequest, default);
        Assert.AreEqual(replay.Fingerprint, again.Fingerprint);
        Assert.AreEqual(1, operation.Calls);
        using var workspace = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(fixture.Context);
        Assert.IsEmpty(await factory.Services.GetRequiredService<IResourceStore>().ListExactAsync<DataSourceAcquisitionResource>(
            ResourceScopeRef.Workspace(fixture.Context.WorkspaceId), DataSourceResourceKinds.DataSourceAcquisition, 0, 100, default));
    }

    [TestMethod]
    public async Task InvalidRawSampleIsInspectableButCannotBecomeAConformantPreview()
    {
        var operation = new SampleExecutor { Invalid = true };
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<ISourceOperationSampleExecutor>(operation))));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var owner = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var samples = factory.Services.GetRequiredService<SourceSampleService>();
        var sample = await samples.AcquireAsync(new(new(fixture.OperationReference, JsonSerializer.SerializeToElement(new { dataset = "raw" }))), default);
        await samples.ProcessNextAsync(default);
        var raw = await samples.GetAsync(sample.Id, default);
        Assert.AreEqual(SourceSampleState.Invalid, raw.State);
        Assert.IsNotNull(raw.Value);
        var error = await Assert.ThrowsExactlyAsync<DataSourceAcquisitionException>(() => samples.ReplayAsync(sample.Id, new(ResourceNamespace.Default, []), default));
        Assert.AreEqual("source_sample_not_conformant", error.Code);
        Assert.AreEqual(1, operation.Calls);
    }

    [TestMethod]
    public async Task SampleIdentityCannotCrossWorkspaceOrPrincipalAndDeletePreventsQueuedExecution()
    {
        var operation = new SampleExecutor();
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<ISourceOperationSampleExecutor>(operation))));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        using var owner = scopes.PushInstance(fixture.Context);
        var samples = factory.Services.GetRequiredService<SourceSampleService>();
        var sample = await samples.AcquireAsync(new(new(fixture.OperationReference, JsonSerializer.SerializeToElement(new { dataset = "raw" }))), default);
        using (scopes.Push(fixture.Context))
        {
            var error = await Assert.ThrowsExactlyAsync<DataSourceAcquisitionException>(() => samples.GetAsync(sample.Id, default));
            Assert.AreEqual("source_sample_unavailable", error.Code);
        }
        var otherWorkspace = await GetBootstrapContextAsync(factory);
        using (scopes.PushInstance(otherWorkspace))
        {
            Assert.AreNotEqual(fixture.Context.WorkspaceId, otherWorkspace.WorkspaceId);
            var foreign = await Assert.ThrowsExactlyAsync<DataSourceAcquisitionException>(() => samples.GetAsync(sample.Id, default));
            Assert.AreEqual("source_sample_unavailable", foreign.Code);
        }
        var sampleStore = factory.Services.GetRequiredService<ISourceSampleStore>();
        sampleStore.Put(sample with { PrincipalId = Guid.NewGuid() });
        var foreignPrincipal = await Assert.ThrowsExactlyAsync<DataSourceAcquisitionException>(() => samples.GetAsync(sample.Id, default));
        Assert.AreEqual("source_sample_unavailable", foreignPrincipal.Code);
        sampleStore.Put(sample);
        await samples.DeleteAsync(sample.Id, default);
        await samples.ProcessNextAsync(default);
        Assert.AreEqual(0, operation.Calls);
        var missing = await Assert.ThrowsExactlyAsync<DataSourceAcquisitionException>(() => samples.GetAsync(sample.Id, default));
        Assert.AreEqual("source_sample_unavailable", missing.Code);
    }

    [TestMethod]
    public async Task PreviewStopsBeforeAnArbitraryFlowAndKeepsItsInput()
    {
        var operation = new SampleExecutor();
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<ISourceOperationSampleExecutor>(operation))));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var owner = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var samples = factory.Services.GetRequiredService<SourceSampleService>();
        var sample = await samples.AcquireAsync(new(new(fixture.OperationReference, JsonSerializer.SerializeToElement(new { dataset = "raw" }))), default);
        await samples.ProcessNextAsync(default);
        // A non-transformation Flow is rejected without executing any of its nodes.
        var preview = await samples.ReplayAsync(sample.Id, new(ResourceNamespace.Default, [new() { Flow = new("fixture-acquire", FlowCallVersionStrategy.Exact, "1.0.0", ScopeRef: Owner) }]), default);
        Assert.AreEqual("preview_flow_unsupported", preview.ErrorCode);
        Assert.HasCount(1, preview.Values);
        Assert.AreEqual(1, operation.Calls);
    }

    [TestMethod]
    public async Task ExpiredSamplesCannotBeReadOrReplayedAndDoNotReacquireData()
    {
        var operation = new SampleExecutor();
        var clock = new NativeAccessClock(TimeProvider.System.GetUtcNow());
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<ISourceOperationSampleExecutor>(operation));
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
        }));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var owner = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var samples = factory.Services.GetRequiredService<SourceSampleService>();
        var sample = await samples.AcquireAsync(new(new(fixture.OperationReference, JsonSerializer.SerializeToElement(new { dataset = "raw" }))), default);
        await samples.ProcessNextAsync(default);
        clock.Now += SourceSampleService.Lifetime + TimeSpan.FromSeconds(1);
        var error = await Assert.ThrowsExactlyAsync<DataSourceAcquisitionException>(() => samples.GetAsync(sample.Id, default));
        Assert.AreEqual("source_sample_unavailable", error.Code);
        Assert.AreEqual(1, operation.Calls);
    }

    [TestMethod]
    public async Task PreparingDirectKnowledgeInputUsesTheRawSampleAndTransformsOnceWithoutProductionAcquisition()
    {
        var operation = new SampleExecutor();
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<ISourceOperationSampleExecutor>(operation))));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var owner = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var desired = Source("direct-preview", fixture, JsonSerializer.SerializeToElement(new { dataset = "raw" }));
        desired = desired with
        {
            Definition = desired.Definition with
            {
                Dataset = desired.Definition.Dataset! with
                {
                    Storage = null,
                    Lifecycle = new() { AllowDirect = true },
                    Transformations = [new() { Function = new("json-project-builtin", "1.0.0", Owner), Parameters = JsonSerializer.SerializeToElement(new { properties = new[] { new { name = "prepared", path = "/dataset" } } }) }]
                }
            }
        };
        var source = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(desired, default);
        var samples = factory.Services.GetRequiredService<SourceSampleService>();
        var raw = await samples.AcquireAsync(new(source.Value.Definition.Dataset!.Acquisition), default);
        await samples.ProcessNextAsync(default);
        var prepared = await samples.PrepareDataSourceAsync(new(new(source.Value.Name, Owner), raw.Id), default);
        Assert.IsFalse(prepared.IsRawOperationResponse);
        Assert.AreEqual(raw.Id, prepared.PreparedInput!.RawSampleId);
        var bytes = Convert.FromBase64String(prepared.Value!.Value.GetProperty("artifacts")[0].GetProperty("contentBase64").GetString()!);
        var consumed = JsonSerializer.Deserialize<JsonElement>(bytes);
        Assert.AreEqual("raw", consumed[0].GetProperty("prepared").GetString());
        Assert.IsFalse(consumed[0].TryGetProperty("dataset", out _));
        Assert.AreEqual(1, operation.Calls);
        using var workspace = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(fixture.Context);
        Assert.IsEmpty(await factory.Services.GetRequiredService<IResourceStore>().ListExactAsync<DataSourceAcquisitionResource>(
            ResourceScopeRef.Workspace(fixture.Context.WorkspaceId), DataSourceResourceKinds.DataSourceAcquisition, 0, 100, default));
    }

    private sealed class SampleExecutor : ISourceOperationSampleExecutor
    {
        public int Calls { get; private set; }
        public bool Invalid { get; init; }
        public Task<JsonElement> ExecuteAsync(DataSourceOperationAcquisitionBinding binding, ResourceScopeRef consumer, string correlationId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(Invalid ? JsonSerializer.SerializeToElement(new { dataset = 42 }) : binding.Parameters.Clone());
        }
    }
}
