using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.Infrastructure.Storage;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class DataSourceDatasetTests : ResourceManagementApiTestBase
{
    private static readonly ResourceScopeRef Owner = ResourceScopeRef.Instance;
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DisabledNativeKnowledgeDraftRetainsValidationAndRequiresSupportedCompositionBeforeActivation(bool workspaceOwner)
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        var bootstrap = await GetBootstrapContextAsync(factory);
        var workspace = ResourceScopeRef.Workspace(bootstrap.WorkspaceId);
        var knowledgeOwner = workspaceOwner ? workspace : Owner;
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var sources = factory.Services.GetRequiredService<DataSourceManagementService>();
        var input = await sources.CreateAsync(Source("draft-input", fixture, JsonSerializer.SerializeToElement(new { dataset = "draft" })), default);
        using var client = factory.CreateClient();
        SelectConsoleContext(client, knowledgeOwner);
        var properties = new KnowledgeSourceProperties
        {
            DisplayName = "Prepared Knowledge",
            Enabled = false,
            ProjectionFlow = new() { Name = "knowledge-projection-builtin", ScopeRef = Owner, Version = "1.0.0", UseActiveVersion = false },
            RetrievalFlow = new() { Name = "knowledge-retrieval-builtin", ScopeRef = Owner, Version = "1.0.0", UseActiveVersion = false },
            DataSources = [new() { Name = "dataset", DataSource = new(input.Value.Name, Owner) }]
        };
        var policies = factory.Services.GetRequiredService<IResourceAccessPolicyService>();
        async Task GrantUseAsync(Resource resource) => _ = await policies.PutAsync(new(new(resource.Uid,
            resource.ScopeRef!.Value, resource.Namespace, resource.Kind),
            [new(workspace, [ResourceAccessAction.Discover, ResourceAccessAction.Use], true)]), null, default);
        if (workspaceOwner)
        {
            using var denied = await client.PostAsJsonAsync("/api/knowledgesources", new CreateKnowledgeSourceRequest("denied-draft", properties, ScopeRef: knowledgeOwner));
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, denied.StatusCode, await denied.Content.ReadAsStringAsync());
            Assert.Contains("data_source_not_found", await denied.Content.ReadAsStringAsync());
            await GrantUseAsync(input.Value);
        }
        using var created = await client.PostAsJsonAsync("/api/knowledgesources", new CreateKnowledgeSourceRequest("native-draft", properties, ScopeRef: knowledgeOwner));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
        var draft = (await created.Content.ReadFromJsonAsync<KnowledgeSourceResource>())!;
        Assert.IsFalse(draft.Definition.Enabled);
        Assert.IsNull(draft.Composition);
        Assert.AreEqual("False", draft.Status!.Conditions.Single(condition => condition.Type == "Ready").Status);
        Assert.AreEqual(input.Value.Name, draft.Definition.DataSources.Single().DataSource.Name);
        var path = $"/api/knowledgesources/native-draft?scopeRef={Uri.EscapeDataString(knowledgeOwner.Value)}";
        var readiness = (await client.GetFromJsonAsync<KnowledgeSourceReadiness>($"/api/knowledgesources/native-draft/readiness?scopeRef={Uri.EscapeDataString(knowledgeOwner.Value)}"))!;
        Assert.IsFalse(readiness.Ready);
        Assert.IsFalse(readiness.Enabled);

        using var editRequest = new HttpRequestMessage(HttpMethod.Put, path)
        { Content = JsonContent.Create(new PutKnowledgeSourceRequest(properties with { DisplayName = "Edited draft" })) };
        editRequest.Headers.IfMatch.Add(created.Headers.ETag!);
        using var edited = await client.SendAsync(editRequest);
        Assert.AreEqual(HttpStatusCode.OK, edited.StatusCode, await edited.Content.ReadAsStringAsync());
        var saved = (await edited.Content.ReadFromJsonAsync<KnowledgeSourceResource>())!;
        Assert.AreEqual("Edited draft", saved.Definition.DisplayName);
        Assert.IsFalse(saved.Definition.Enabled);
        Assert.IsNull(saved.Composition);
        using var enableRequest = new HttpRequestMessage(HttpMethod.Put,
            $"/api/knowledgesources/native-draft/enabled?scopeRef={Uri.EscapeDataString(knowledgeOwner.Value)}")
        { Content = JsonContent.Create(new SetKnowledgeSourceEnabledRequest(true)) };
        enableRequest.Headers.IfMatch.Add(edited.Headers.ETag!);
        using var enabled = await client.SendAsync(enableRequest);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, enabled.StatusCode, await enabled.Content.ReadAsStringAsync());
        Assert.Contains("knowledge_projection_dataset_input_unsupported", await enabled.Content.ReadAsStringAsync());
        using var enabledPutRequest = new HttpRequestMessage(HttpMethod.Put, path)
        { Content = JsonContent.Create(new PutKnowledgeSourceRequest(saved.Definition with { Enabled = true })) };
        enabledPutRequest.Headers.IfMatch.Add(edited.Headers.ETag!);
        using var enabledPut = await client.SendAsync(enabledPutRequest);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, enabledPut.StatusCode, await enabledPut.Content.ReadAsStringAsync());
        var unchanged = (await client.GetFromJsonAsync<KnowledgeSourceResource>(path))!;
        Assert.AreEqual(saved.Generation, unchanged.Generation);
        Assert.IsFalse(unchanged.Definition.Enabled);

        // An unsupported native input must not hide invalid or foreign references later in the graph.
        var invalidDefinitions = new[]
        {
            properties with { DataSources = [properties.DataSources[0], new() { Name = "foreign", DataSource = new("foreign", ResourceScopeRef.Workspace(Guid.NewGuid())) }] },
            properties with { DataSources = [properties.DataSources[0] with { TransformationFlow = new() { Name = "missing-transformation", ScopeRef = Owner } }] }
        };
        for (var index = 0; index < invalidDefinitions.Length; index++)
        {
            using var invalid = await client.PostAsJsonAsync("/api/knowledgesources", new CreateKnowledgeSourceRequest($"invalid-draft-{index}", invalidDefinitions[index], ScopeRef: knowledgeOwner));
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalid.StatusCode, await invalid.Content.ReadAsStringAsync());
            Assert.DoesNotContain("knowledge_projection_dataset_input_unsupported", await invalid.Content.ReadAsStringAsync());
            using var absent = await client.GetAsync($"/api/knowledgesources/invalid-draft-{index}?scopeRef={Uri.EscapeDataString(knowledgeOwner.Value)}");
            Assert.AreEqual(HttpStatusCode.NotFound, absent.StatusCode);
        }
        if (!workspaceOwner) await GrantUseAsync(saved);
        SelectConsoleContext(client, workspace);
        using var projection = await client.PostAsJsonAsync($"/api/knowledgesources/native-draft/projections?scopeRef={Uri.EscapeDataString(knowledgeOwner.Value)}", new StartKnowledgeProjectionRequest());
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, projection.StatusCode, await projection.Content.ReadAsStringAsync());
        Assert.Contains("knowledge_projection_source_disabled", await projection.Content.ReadAsStringAsync());
        Assert.IsEmpty((await client.GetFromJsonAsync<KnowledgeProjectionResource[]>($"/api/knowledgesources/native-draft/projections?scopeRef={Uri.EscapeDataString(knowledgeOwner.Value)}"))!);
        SelectConsoleContext(client, knowledgeOwner);

        // Replacing the unsupported input resolves a complete fresh graph before enabling.
        var legacy = await sources.CreateAsync(new DataSourceResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = DataSourceResourceKinds.DataSource,
            Metadata = new() { Name = "supported-input" },
            ScopeRef = Owner,
            Definition = new()
            {
                DisplayName = "Supported input",
                Profile = new("artifact-import-builtin", Owner),
                Configuration = JsonSerializer.SerializeToElement(new { artifactIds = Array.Empty<string>() })
            }
        }, default);
        if (workspaceOwner) await GrantUseAsync(legacy.Value);
        using var supportedRequest = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = JsonContent.Create(new PutKnowledgeSourceRequest(properties with
            { Enabled = true, DataSources = [properties.DataSources[0] with { DataSource = new(legacy.Value.Name, Owner) }] }))
        };
        supportedRequest.Headers.IfMatch.Add(edited.Headers.ETag!);
        using var supported = await client.SendAsync(supportedRequest);
        Assert.AreEqual(HttpStatusCode.OK, supported.StatusCode, await supported.Content.ReadAsStringAsync());
        var executable = (await supported.Content.ReadFromJsonAsync<KnowledgeSourceResource>())!;
        Assert.IsTrue(executable.Definition.Enabled);
        Assert.IsNotNull(executable.Composition);
        Assert.AreEqual(legacy.Value.Uid, executable.Composition.DataSources.Single().DataSourceUid);
        Assert.AreEqual(legacy.Value.Generation, executable.Composition.DataSources.Single().DataSourceGeneration);
    }

    [TestMethod]
    public async Task RealOperationAcquiresTwoIsolatedDatasetsIntoOneLocalStorageAndRetainsHistory()
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var management = factory.Services.GetRequiredService<DataSourceManagementService>();
        var first = await management.CreateAsync(Source("first", fixture, JsonSerializer.SerializeToElement(new { dataset = "alpha" })), default);
        var second = await management.CreateAsync(Source("second", fixture, JsonSerializer.SerializeToElement(new { dataset = "beta" })), default);
        Assert.IsTrue((await management.GetReadinessAsync(first.Value.Namespace, first.Value.Name, Owner, default)).CanAcquire);
        using var client = factory.CreateClient();
        SelectConsoleContext(client, Owner);
        using var targetsResponse = await client.GetAsync("/api/datasources/first/acquisition-targets?scopeRef=/instance");
        Assert.AreEqual(HttpStatusCode.OK, targetsResponse.StatusCode, await targetsResponse.Content.ReadAsStringAsync());
        Assert.IsEmpty((await targetsResponse.Content.ReadFromJsonAsync<DataSourceAcquisitionTargetView[]>())!);
        using var knowledge = await client.PostAsJsonAsync("/api/knowledgesources", new CreateKnowledgeSourceRequest("native-knowledge", new()
        {
            DisplayName = "Native Knowledge",
            Enabled = true,
            ProjectionFlow = new() { Name = "knowledge-projection-builtin", ScopeRef = Owner, Version = "1.0.0", UseActiveVersion = false },
            RetrievalFlow = new() { Name = "knowledge-retrieval-builtin", ScopeRef = Owner, Version = "1.0.0", UseActiveVersion = false },
            DataSources = [new() { Name = "dataset", DataSource = new(first.Value.Name, Owner) }]
        }, ScopeRef: Owner));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, knowledge.StatusCode, await knowledge.Content.ReadAsStringAsync());
        var problem = await knowledge.Content.ReadFromJsonAsync<JsonElement>();
        Assert.AreEqual("https://agentstration.dev/problems/knowledge_projection_dataset_input_unsupported", problem.GetProperty("type").GetString());
        var one = await AcquireAsync(client, first.Value.Name);
        var two = await AcquireAsync(client, second.Value.Name);
        Assert.AreNotEqual(one.Content!.Key, two.Content!.Key);
        Assert.AreEqual(one.Dataset!.Definition.Storage, two.Dataset!.Definition.Storage);
        Assert.AreEqual(one.Dataset.Definition.Acquisition.Operation, two.Dataset.Definition.Acquisition.Operation);
        Assert.AreNotEqual(one.Dataset.Partition, two.Dataset.Partition);
        var nested = Source("nested", fixture, JsonSerializer.SerializeToElement(new { dataset = "nested" }));
        await Assert.ThrowsAsync<DataSourceValidationException>(() => management.CreateAsync(nested with
        { Definition = nested.Definition with { Dataset = nested.Definition.Dataset! with { Rule = nested.Definition.Dataset.Rule with { Prefix = one.Dataset.Partition } } } }, default));
        Assert.IsNull(one.Composition);
        Assert.AreEqual(fixture.Operation.Uid, one.Dataset.Operation.Uid);
        Assert.AreEqual("alpha", await ReadDatasetAsync(client, one.Name));
        Assert.AreEqual("beta", await ReadDatasetAsync(client, two.Name));
        var history = await client.GetFromJsonAsync<DataSourceAcquisitionResource[]>("/api/datasources/first/acquisitions?scopeRef=/instance");
        Assert.HasCount(1, history!);
        Assert.AreEqual(one.Content.Version, history![0].Content!.Version);

        // Shared placement does not confer the other dataset's content authority.
        var gateway = factory.Services.GetRequiredService<IStorageContentGateway>();
        var target = DataSourceDatasetCompositionService.Target(one, Owner);
        await Assert.ThrowsAsync<Agentstration.Storage.StorageException>(() => gateway.ReadAsync(
            target, two.Content.Reference, 0, 100, default));
        using var overrideCall = await client.PostAsJsonAsync("/api/datasources/first/acquisitions?scopeRef=/instance",
            new StartDataSourceAcquisitionRequest { Parameters = JsonSerializer.SerializeToElement(new { dataset = "beta", store = "other" }) });
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, overrideCall.StatusCode);

        var disabled = await factory.Services.GetRequiredService<ConnectedServiceOperationService>().SetEnabledAsync(
            fixture.OperationReference.Operation, false, fixture.Operation.ETag, default);
        using var refused = await client.PostAsJsonAsync("/api/datasources/first/acquisitions?scopeRef=/instance", new StartDataSourceAcquisitionRequest());
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.IsFalse(disabled.Value.Enabled);
        // Disabling acquisition never deletes an already acquired dataset or requires executing the origin to read it.
        Assert.AreEqual("alpha", await ReadDatasetAsync(client, one.Name));
        Assert.AreEqual(DataSourceAcquisitionState.Succeeded,
            (await factory.Services.GetRequiredService<DataSourceAcquisitionService>().GetAsync(ResourceNamespace.Default, one.Name, default)).Value.State);
        var bootstrap = await GetBootstrapContextAsync(factory);
        SelectConsoleContext(client, ResourceScopeRef.Workspace(bootstrap.WorkspaceId));
        using var foreign = await client.GetAsync($"/api/datasourceacquisitions/{one.Name}/content");
        Assert.AreEqual(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    [TestMethod]
    public async Task ContentWithAnUnversionedWriteReceiptIsVerifiedBeforeReturningTheRequestedRange()
    {
        var gateway = new UnversionedWriteStorage();
        await using var baseFactory = Factory();
        await using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<IStorageContentGateway>(gateway)));
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var source = await factory.Services.GetRequiredService<DataSourceManagementService>()
            .CreateAsync(Source("receipt-test", fixture, JsonSerializer.SerializeToElement(new { dataset = "alpha" })), default);
        using var client = factory.CreateClient();
        SelectConsoleContext(client, Owner);
        var acquisition = await AcquireAsync(client, source.Value.Name);
        Assert.IsNotNull(acquisition.Content);
        Assert.IsNull(acquisition.Content.Version);
        Assert.IsNotNull(acquisition.Content.Sha256);

        var path = $"/api/datasourceacquisitions/{acquisition.Name}/content?offset=2&maximumBytes=4";
        var response = await client.GetFromJsonAsync<StorageObjectContent>(path);
        Assert.IsNotNull(response);
        Assert.AreEqual(2L, response.Offset);
        Assert.IsFalse(response.EndOfObject);
        Assert.AreEqual(acquisition.Content.Sha256, response.Item.Sha256);
        CollectionAssert.AreEqual(gateway.Bytes[2..6], Convert.FromBase64String(response.ContentBase64));

        gateway.Bytes[^2] ^= 1; // Alter a byte outside the requested range without changing the length or metadata.
        using var refused = await client.GetAsync(path);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("data_source_content_version_unavailable", await refused.Content.ReadAsStringAsync());
    }

    private sealed class UnversionedWriteStorage : IStorageContentGateway
    {
        private StorageObject? written;
        public byte[] Bytes { get; private set; } = [];

        private StorageObject Listed => written! with { Version = "current-version", Sha256 = null, Reference = "fresh-reference" };

        public Task ValidateAsync(StorageContentRequest target, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<StorageObjectPage> ListAsync(StorageContentRequest target, string prefix, int limit, string? cursor = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new StorageObjectPage(written is null ? [] : [Listed], null));

        public Task<StorageObject> StatAsync(StorageContentRequest target, string reference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StorageObjectContent> ReadAsync(StorageContentRequest target, string reference, long offset, int maximumBytes,
            CancellationToken cancellationToken = default)
        {
            Assert.AreEqual(Listed.Reference, reference);
            var length = Math.Min(maximumBytes, Bytes.Length - (int)offset);
            return Task.FromResult(new StorageObjectContent(Listed, offset,
                Convert.ToBase64String(Bytes.AsSpan((int)offset, length)), offset + length == Bytes.Length));
        }

        public Task<StorageObject> WriteAsync(StorageContentRequest target, StorageWrite request, CancellationToken cancellationToken = default)
        {
            Bytes = Convert.FromBase64String(request.ContentBase64);
            written = new(request.Key, null, Bytes.Length, Convert.ToHexStringLower(SHA256.HashData(Bytes)), "write-reference");
            return Task.FromResult(written);
        }
    }

    [TestMethod]
    public async Task InvalidCompositionCannotInventParametersOverrideConnectionOrEscapeTheStorageEnvelope()
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var management = factory.Services.GetRequiredService<DataSourceManagementService>();
        var source = Source("invalid", fixture, Empty);
        var invalidParameters = await Assert.ThrowsAsync<DataSourceValidationException>(() => management.CreateAsync(source, default));
        Assert.AreEqual("data_source_parameters_invalid", invalidParameters.Code);
        var valid = Source("valid", fixture, JsonSerializer.SerializeToElement(new { dataset = "alpha" }));
        foreach (var prefix in new[] { "../", "/", "foreign", "a/../../", "datasets/other/" })
            await Assert.ThrowsAsync<DataSourceValidationException>(() => management.CreateAsync(valid with
            {
                Definition = valid.Definition with
                {
                    Dataset = valid.Definition.Dataset! with
                    { Rule = valid.Definition.Dataset.Rule with { Prefix = prefix } }
                }
            }, default));
        await Assert.ThrowsAsync<DataSourceValidationException>(() => management.CreateAsync(valid with
        { Definition = valid.Definition with { Configuration = JsonSerializer.SerializeToElement(new { credential = "override" }) } }, default));
        await Assert.ThrowsAsync<DataSourceValidationException>(() => management.CreateAsync(valid with
        { Definition = valid.Definition with { Profile = new("legacy") } }, default));
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var originalStorage = (await store.GetExactAsync<StorageResource>(fixture.Storage.Storage, default))!.Value;
        var bounded = await factory.Services.GetRequiredService<Agentstration.Storage.StorageManagementService>().CreateAsync(originalStorage with
        {
            Uid = Guid.Empty,
            Governance = ResourceGovernance.User,
            Metadata = new() { Name = "bounded" },
            Definition = originalStorage.Definition with { Envelope = JsonSerializer.SerializeToElement(new { list = true, stat = true, read = true, write = true, prefix = "permitted/" }) }
        }, default);
        var selected = new StorageRevisionReference(new(Owner, bounded.Value.Namespace, bounded.Value.Kind, bounded.Value.Name),
            bounded.Value.Uid, bounded.Value.Generation, bounded.Value.ObservedState!.BindingDigest!);
        await Assert.ThrowsAsync<Agentstration.Storage.StorageException>(() => management.CreateAsync(valid with
        { Definition = valid.Definition with { Dataset = valid.Definition.Dataset! with { Storage = selected } } }, default));
    }

    [TestMethod]
    public async Task EmptyInputIsValidAndConcurrentIdempotentStartsHaveOneDurableIdentity()
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services, emptyInput: true);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var source = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(Source("empty", fixture, Empty), default);
        var service = factory.Services.GetRequiredService<DataSourceAcquisitionService>();
        var starts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => service.StartAsync(
            source.Value.Namespace, source.Value.Name, Owner, Empty, null, "same-request", default)));
        Assert.AreEqual(1, starts.Select(item => item.Value.Uid).Distinct().Count());
        using var client = factory.CreateClient();
        SelectConsoleContext(client, Owner);
        var completed = await WaitAsync(client, starts[0].Value.Name);
        Assert.AreEqual(DataSourceAcquisitionState.Succeeded, completed.State, completed.ErrorMessage);
        using var content = await client.GetAsync($"/api/datasourceacquisitions/{completed.Name}/content");
        Assert.AreEqual(HttpStatusCode.OK, content.StatusCode, await content.Content.ReadAsStringAsync());
    }

    private sealed record Fixture(RequestContext Context, ConnectedServiceOperationResource Operation,
        ConnectedServiceOperationRevisionReference OperationReference, StorageRevisionReference Storage);

    [TestMethod]
    public async Task PendingAcquisitionRejectsConcurrentStartAndCancellationPublishesNoContent()
    {
        await using var factory = Factory();
        Fixture fixture;
        await using (var worker = await AwpTestWorker.StartAsync(factory)) fixture = await PrepareAsync(factory.Services);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var source = (await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(
            Source("cancel", fixture, JsonSerializer.SerializeToElement(new { dataset = "cancelled" })), default)).Value;
        var acquisitions = factory.Services.GetRequiredService<DataSourceAcquisitionService>();
        var accepted = await acquisitions.StartAsync(source.Namespace, source.Name, Owner, Empty, null, null, default);
        var concurrent = await Assert.ThrowsAsync<DataSourceAcquisitionException>(() => acquisitions.StartAsync(
            source.Namespace, source.Name, Owner, Empty, null, null, default));
        Assert.AreEqual("data_source_acquisition_already_running", concurrent.Code);
        var cancelled = await acquisitions.CancelAsync(accepted.Value.Namespace, accepted.Value.Name, default);
        Assert.AreEqual(DataSourceAcquisitionState.Cancelled, cancelled.Value.State);
        Assert.IsNull(cancelled.Value.Content);
        await Assert.ThrowsAsync<DataSourceAcquisitionException>(() => factory.Services.GetRequiredService<DataSourceDatasetAcquisitionService>()
            .ReadAsync(cancelled.Value, 0, 100, default));
    }

    [TestMethod]
    public async Task ActualHostRestartRecoversAcceptedAcquisitionAndPreservesExactDatasetContentAndIdentities()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agentstration-dataset-restart-" + Guid.NewGuid().ToString("N"));
        Fixture fixture;
        DataSourceResource source;
        DataSourceAcquisitionResource accepted;
        try
        {
            await using (var first = Factory().WithWebHostBuilder(builder => builder.UseEnvironment("Development").UseSetting("Agentstration:Authentication:Mode", "Development")
                .UseSetting("Data:Directory", directory).UseSetting("Data:ControlPlanePath", Path.Combine(directory, "control-plane.db"))
                .UseSetting("Data:WorkPlanePath", Path.Combine(directory, "work-plane.db")).UseSetting("Data:FlowPath", Path.Combine(directory, "flow-plane.db"))
                .UseSetting("Data:RuntimePath", Path.Combine(directory, "runtime-plane.db"))))
            {
                await using (var worker = await AwpTestWorker.StartAsync(first)) fixture = await PrepareAsync(first.Services);
                using var scope = first.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
                source = (await first.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(
                    Source("restart", fixture, JsonSerializer.SerializeToElement(new { dataset = "retained" })), default)).Value;
                accepted = (await first.Services.GetRequiredService<DataSourceAcquisitionService>().StartAsync(
                    source.Namespace, source.Name, Owner, Empty, null, "restart-request", default)).Value;
                Assert.AreEqual(DataSourceAcquisitionState.Pending, accepted.State);
            }
            await using (var second = Factory().WithWebHostBuilder(builder => builder.UseEnvironment("Development").UseSetting("Agentstration:Authentication:Mode", "Development")
                .UseSetting("Data:Directory", directory).UseSetting("Data:ControlPlanePath", Path.Combine(directory, "control-plane.db"))
                .UseSetting("Data:WorkPlanePath", Path.Combine(directory, "work-plane.db")).UseSetting("Data:FlowPath", Path.Combine(directory, "flow-plane.db"))
                .UseSetting("Data:RuntimePath", Path.Combine(directory, "runtime-plane.db"))))
            {
                await using var worker = await AwpTestWorker.StartAsync(second);
                var scopes = second.Services.GetRequiredService<IRequestContextScopeFactory>();
                using (scopes.PushSystem()) await second.Services.GetRequiredService<DataSourceAcquisitionService>().RecoverPendingAsync(default);
                using var caller = scopes.PushInstance(fixture.Context);
                DataSourceAcquisitionResource completed = accepted;
                for (var count = 0; count < 300; count++)
                {
                    completed = (await second.Services.GetRequiredService<DataSourceAcquisitionService>().GetAsync(source.Namespace, accepted.Name, default)).Value;
                    if (completed.State == DataSourceAcquisitionState.Succeeded) break;
                    Assert.IsNull(completed.ErrorCode, completed.ErrorMessage);
                    await Task.Delay(20);
                }
                Assert.AreEqual(DataSourceAcquisitionState.Succeeded, completed.State);
                Assert.AreEqual(accepted.Uid, completed.Uid);
                Assert.AreEqual(accepted.FlowRunId, completed.FlowRunId);
                Assert.AreEqual(source.Uid, completed.DataSourceUid);
                Assert.AreEqual(fixture.Storage.StorageUid, completed.Dataset!.Definition.Storage.StorageUid);
                Assert.AreEqual(fixture.Operation.Uid, completed.Dataset.Operation.Uid);
                var content = await second.Services.GetRequiredService<DataSourceDatasetAcquisitionService>().ReadAsync(completed, 0, 65536, default);
                Assert.Contains("retained", Encoding.UTF8.GetString(Convert.FromBase64String(content.ContentBase64)));

            }
            // A second restart reads the retained bytes and receipts without a worker or another origin execution.
            await using (var third = Factory().WithWebHostBuilder(builder => builder.UseEnvironment("Development").UseSetting("Agentstration:Authentication:Mode", "Development")
                .UseSetting("Data:Directory", directory).UseSetting("Data:ControlPlanePath", Path.Combine(directory, "control-plane.db"))
                .UseSetting("Data:WorkPlanePath", Path.Combine(directory, "work-plane.db")).UseSetting("Data:FlowPath", Path.Combine(directory, "flow-plane.db"))
                .UseSetting("Data:RuntimePath", Path.Combine(directory, "runtime-plane.db"))))
            {
                _ = await GetBootstrapContextAsync(third);
                using var caller = third.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
                var acquisitions = third.Services.GetRequiredService<DataSourceAcquisitionService>();
                var history = await acquisitions.ListAsync(source.Namespace, source.Name, Owner, default);
                Assert.HasCount(1, history);
                Assert.AreEqual(accepted.Uid, history[0].Uid);
                var content = await third.Services.GetRequiredService<DataSourceDatasetAcquisitionService>().ReadAsync(history[0], 0, 65536, default);
                Assert.Contains("retained", Encoding.UTF8.GetString(Convert.FromBase64String(content.ContentBase64)));

            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<Fixture> PrepareAsync(IServiceProvider services, bool emptyInput = false)
    {
        var bootstrap = await services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        await services.GetRequiredService<IWorkspacePlatformResourceProvisioner>().EnsureAllAsync(default);
        var workspace = await services.GetRequiredService<IOperationalTopologyProvisioner>().GetInstanceOperationsWorkspaceAsync(default);
        var context = new RequestContext(bootstrap.PrincipalId, workspace.TenantId, workspace.Id);
        using var scope = services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(context);
        var input = emptyInput ? JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false })
            : JsonSerializer.SerializeToElement(new { type = "object", properties = new { dataset = new { type = "string", minLength = 1 } }, required = new[] { "dataset" }, additionalProperties = false });
        var flow = await PublishAsync(services, "fixture-acquire", DataSourceDatasetCompositionService.FlowContract,
            emptyInput ? Empty : JsonSerializer.SerializeToElement(new { dataset = "${input.parameters.dataset}" }));
        var definitionSchema = JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = true });
        var type = await services.GetRequiredService<ConnectedServiceOperationTypeManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ConnectedServiceResourceKinds.ConnectedServiceOperationType,
            Metadata = new() { Name = "fixture-operation" },
            ScopeRef = Owner,
            Definition = new()
            {
                DisplayName = "Fixture operation",
                PreferredVersion = "1.0.0",
                Versions =
            [new("1.0.0", AepSchemaDigest.Compute(definitionSchema), definitionSchema,
                ConnectedServiceOperationInteraction.RequestResponse, new("fixture.read", "1.0.0")) { Acquisition = new(flow) }]
            }
        }, default);
        var typeRef = new ConnectedServiceOperationTypeReference
        {
            Type = new(type.Value.Name, Owner),
            TypeUid = type.Value.Uid,
            Version = "1.0.0",
            SchemaDigest = type.Value.Definition.Versions[0].SchemaDigest
        };
        var discoveryFlow = await PublishAsync(services, "fixture-discover", ConnectedServiceOperationDiscoveryContracts.V1,
            JsonSerializer.SerializeToElement(new ConnectedServiceOperationDiscoveryManifest
            {
                Operations =
            [new() { ExternalId = "fixture.read", DisplayName = "Read fixture", Type = typeRef,
                Definition = JsonSerializer.SerializeToElement(new { readOnly = true, inputSchema = input, outputSchema = input }) }]
            }));
        var serviceTypes = services.GetRequiredService<ConnectedServiceTypeManagementService>();
        var version = new ConnectedServiceTypeVersionDescriptor("1.0.0", AepSchemaDigest.Compute(definitionSchema), definitionSchema, [], [])
        { OperationDiscovery = new() { Flow = discoveryFlow, ProducedTypes = [typeRef] } };
        var serviceType = await serviceTypes.CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ConnectedServiceResourceKinds.ConnectedServiceType,
            Metadata = new() { Name = "fixture-service" },
            ScopeRef = Owner,
            Definition = new() { DisplayName = "Fixture service", PreferredVersion = "1.0.0", Versions = [version] }
        }, default);
        var service = await services.GetRequiredService<ConnectedServiceManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ConnectedServiceResourceKinds.ConnectedService,
            Metadata = new() { Name = "fixture" },
            ScopeRef = Owner,
            Definition = new()
            {
                DisplayName = "Offline fixture",
                Type = new()
                {
                    Type = new(serviceType.Value.Name, Owner),
                    TypeUid = serviceType.Value.Uid,
                    Version = "1.0.0",
                    SchemaDigest = version.SchemaDigest
                },
                Options = Empty
            }
        }, default);
        var discoveries = services.GetRequiredService<ConnectedServiceOperationDiscoveryService>();
        var discovery = await discoveries.StartAsync(Owner, service.Value.Name, null, null, default);
        for (var count = 0; count < 300; count++)
        {
            discovery = await discoveries.GetAsync(ResourceNamespace.Default, discovery.Value.Name, default);
            if (discovery.Value.State == ConnectedServiceOperationDiscoveryState.Succeeded) break;
            if (discovery.Value.ErrorCode is not null) Assert.Fail(discovery.Value.ErrorMessage ?? discovery.Value.ErrorCode);
            await Task.Delay(20);
        }
        Assert.AreEqual(ConnectedServiceOperationDiscoveryState.Succeeded, discovery.Value.State, discovery.Value.ErrorMessage);
        var operation = (await discoveries.ListOperationsAsync(Owner, service.Value.Name, default)).Single();
        Assert.IsFalse(operation.Enabled);
        var address = new ScopedResourceAddress(Owner, operation.Namespace, operation.Kind, operation.Name);
        var stored = (await discoveries.GetOperationAsync(Owner, operation.Namespace, operation.Name, default))!;
        var enabled = await services.GetRequiredService<ConnectedServiceOperationService>().SetEnabledAsync(address, true, stored.ETag, default);
        var storage = (await services.GetRequiredService<IResourceStore>().GetExactAsync<StorageResource>(
            new(Owner, ResourceNamespace.Default, StorageResourceKinds.Storage, LocalStorageDefaultsProvisioner.DefaultName), default))!.Value;
        return new(context, enabled.Value, new(address, enabled.Value.Uid, enabled.Value.Generation, enabled.Value.Definition.DefinitionDigest),
            new(new(Owner, storage.Namespace, storage.Kind, storage.Name), storage.Uid, storage.Generation, storage.ObservedState!.BindingDigest!));
    }

    private static DataSourceResource Source(string name, Fixture fixture, JsonElement parameters) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = DataSourceResourceKinds.DataSource,
        Metadata = new() { Name = name },
        ScopeRef = Owner,
        Definition = new() { DisplayName = name, Dataset = new(fixture.Storage, new(Write: true), new(fixture.OperationReference, parameters)) }
    };

    private static async Task<ConnectedServiceDiscoveryFlowReference> PublishAsync(IServiceProvider services, string name, string contract, JsonElement output)
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object" });
        var flows = services.GetRequiredService<FlowService>();
        var flow = await flows.CreateExactAsync(Owner, new CreateFlowCommand(name, null, "1.0.0", true,
            new WorkflowFlowDefinition("input", [new FlowNode("input", FlowNodeKind.Input)], []),
            new Dictionary<string, string> { [FlowMetadataKeys.Contract] = contract }, new FlowGraphDefinition
            {
                EntryStep = "input",
                InputSchema = schema,
                OutputSchema = schema,
                Steps = [new InputFlowStepDefinition { Name = "input", Schema = schema },
                    new OutputFlowStepDefinition { Name = "output", OutputMapping = output }],
                Transitions = [new("input-output", "input", "completed", "output")]
            }), ResourceNamespace.Default, default);
        var version = await flows.PublishVersionExactAsync(Owner, new(name), "1.0.0", true, default);
        return new()
        {
            Flow = new(name, Owner),
            FlowUid = flow.Value.Uid,
            Version = "1.0.0",
            DefinitionHash = version.Value.DefinitionHash!,
            Contract = contract
        };
    }

    private static async Task<DataSourceAcquisitionResource> AcquireAsync(HttpClient client, string source)
    {
        using var response = await client.PostAsJsonAsync($"/api/datasources/{source}/acquisitions?scopeRef=/instance", new StartDataSourceAcquisitionRequest());
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, await response.Content.ReadAsStringAsync());
        return await WaitAsync(client, (await response.Content.ReadFromJsonAsync<DataSourceAcquisitionResource>())!.Name);
    }

    private static async Task<DataSourceAcquisitionResource> WaitAsync(HttpClient client, string name)
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            var value = (await client.GetFromJsonAsync<DataSourceAcquisitionResource>($"/api/datasourceacquisitions/{name}"))!;
            if (value.State == DataSourceAcquisitionState.Succeeded) return value;
            Assert.IsNull(value.ErrorCode, value.ErrorMessage);
            await Task.Delay(20);
        }
        throw new AssertFailedException("The offline acquisition did not complete.");
    }

    private static async Task<string?> ReadDatasetAsync(HttpClient client, string name)
    {
        var content = (await client.GetFromJsonAsync<StorageObjectContent>($"/api/datasourceacquisitions/{name}/content"))!;
        using var json = JsonDocument.Parse(Convert.FromBase64String(content.ContentBase64));
        return json.RootElement.GetProperty("dataset").GetString();
    }
}
