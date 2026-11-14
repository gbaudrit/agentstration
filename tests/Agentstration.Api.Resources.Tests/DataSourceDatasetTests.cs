using Agentstration.Infrastructure.Artifacts;
using Agentstration.Artifacts;
using Agentstration.Artifacts.Contracts;
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
using Agentstration.Infrastructure.Knowledge;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Agentstration.Knowledge.Contracts;
using Agentstration.Knowledge;
using Agentstration.Storage;
using Agentstration.Tools.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed partial class DataSourceDatasetTests : ResourceManagementApiTestBase
{
    private static readonly ResourceScopeRef Owner = ResourceScopeRef.Instance;
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    [TestMethod]
    public async Task InstanceKnowledgePublishesLogicalToolsAndExecutesAgainstItsOperationalSnapshot()
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var source = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(
            Source("tool-input", fixture, JsonSerializer.SerializeToElement(new { dataset = "instance-tool-content" })), default);
        using var client = factory.CreateClient();
        SelectConsoleContext(client, Owner);
        _ = await AcquireAsync(client, source.Value.Name);
        var knowledge = await factory.Services.GetRequiredService<KnowledgeSourceManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            Metadata = new() { Name = "instance-tools" },
            ScopeRef = Owner,
            Definition = new()
            {
                DisplayName = "Instance Tools",
                Output = new(fixture.Storage, new(Write: true)),
                DataSources = [new() { Name = "input", DataSource = new(source.Value.Name, Owner) }],
                ProjectionFlow = new() { Name = "knowledge-projection-builtin", ScopeRef = Owner },
                RetrievalFlow = new() { Name = "knowledge-retrieval-builtin", ScopeRef = Owner }
            }
        }, default);
        _ = await factory.Services.GetRequiredService<KnowledgeProjectionService>().StartAsync(
            new(knowledge.Value.Name, ScopeRef: Owner), new(), default);
        using var response = await client.PostAsJsonAsync($"/api/knowledgesources/{knowledge.Value.Name}/tool-exposure?scopeRef=/instance",
            new PublishKnowledgeSourceToolExposureRequest());
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        var exposure = (await response.Content.ReadFromJsonAsync<KnowledgeSourceToolExposureResource>())!;
        Assert.AreEqual(ResourceScopeRef.Workspace(fixture.Context.WorkspaceId), exposure.ScopeRef);
        Assert.AreEqual(Owner, exposure.ToolSet.ScopeRef);
        Assert.HasCount(3, exposure.Operations);
        Assert.IsTrue(exposure.Operations.All(value => value.Tool.ScopeRef == Owner));
        using var reloaded = await client.GetAsync($"/api/knowledgesources/{knowledge.Value.Name}/tool-exposure?scopeRef=/instance");
        Assert.AreEqual(HttpStatusCode.OK, reloaded.StatusCode);
        foreach (var operation in exposure.Operations)
        {
            var request = operation.Operation == KnowledgeSourceOperation.SearchRegex
                ? JsonSerializer.SerializeToElement(new { pattern = "instance-tool-content" }) : Empty;
            using var executed = await client.PostAsJsonAsync($"/api/tools/{operation.Tool.Name}/run?scopeRef=/instance",
                new RunToolRequest(JsonSerializer.SerializeToElement(new { request }), ToolRunMode.Execute));
            Assert.AreEqual(HttpStatusCode.OK, executed.StatusCode, await executed.Content.ReadAsStringAsync());
            var result = (await executed.Content.ReadFromJsonAsync<RunToolResponse>())!;
            Assert.AreEqual("completed", result.Status, JsonSerializer.Serialize(result));
            Assert.Contains(operation.Operation == KnowledgeSourceOperation.GetStatus ? "activeSnapshotId" : "instance-tool-content",
                result.Output!.Value.GetRawText());
            using var overrideSource = await client.PostAsJsonAsync($"/api/tools/{operation.Tool.Name}/run?scopeRef=/instance",
                new RunToolRequest(JsonSerializer.SerializeToElement(new { request, source = new { name = "substituted" } }), ToolRunMode.Execute));
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, overrideSource.StatusCode);
        }

    }

    [TestMethod]
    public async Task NativeProjectionAndRetrievalHonorLowerStorageReadBounds()
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var original = (await factory.Services.GetRequiredService<IResourceStore>().GetExactAsync<StorageResource>(fixture.Storage.Storage, default))!.Value;
        var bounded = await factory.Services.GetRequiredService<StorageManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = StorageResourceKinds.Storage,
            Metadata = new() { Name = "small-read-bound" },
            ScopeRef = Owner,
            Definition = original.Definition with
            {
                Envelope = JsonSerializer.SerializeToElement(new
                { list = true, stat = true, read = true, write = true, maximumReadBytes = 4 })
            }
        }, default);
        var reference = new StorageRevisionReference(new(Owner, bounded.Value.Namespace, bounded.Value.Kind, bounded.Value.Name),
            bounded.Value.Uid, bounded.Value.Generation, StorageDigests.Binding(bounded.Value.Definition));
        var definition = Source("bounded-content", fixture, JsonSerializer.SerializeToElement(new { dataset = "bounded-reading" }));
        var input = await factory.Services.GetRequiredService<DataSourceManagementService>().CreateAsync(definition with
        { Definition = definition.Definition with { Dataset = definition.Definition.Dataset! with { Storage = reference } } }, default);
        using var client = factory.CreateClient();
        SelectConsoleContext(client, Owner);
        _ = await AcquireAsync(client, input.Value.Name);
        var knowledge = await factory.Services.GetRequiredService<KnowledgeSourceManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            Metadata = new() { Name = "bounded-knowledge" },
            ScopeRef = Owner,
            Definition = new()
            {
                DisplayName = "Bounded Knowledge",
                Output = new(reference, new(Write: true)),
                DataSources = [new() { Name = "bounded", DataSource = new(input.Value.Name, Owner) }],
                ProjectionFlow = new() { Name = "knowledge-projection-builtin", ScopeRef = Owner },
                RetrievalFlow = new() { Name = "knowledge-retrieval-builtin", ScopeRef = Owner }
            }
        }, default);
        _ = await factory.Services.GetRequiredService<KnowledgeProjectionService>().StartAsync(new(knowledge.Value.Name, ScopeRef: Owner), new(), default);
        var snapshot = (await factory.Services.GetRequiredService<KnowledgeSnapshotService>().GetActiveAsync(new(knowledge.Value.Name, ScopeRef: Owner), default))!.Snapshot;
        Assert.AreEqual(4, snapshot.Artifacts[0].Content!.Target.Policy.MaximumReadBytes);
        var native = await factory.Services.GetRequiredService<KnowledgeAccessService>().ReadAsync(new(knowledge.Value.Name, ScopeRef: Owner),
            new() { SnapshotId = snapshot.Name }, default);
        Assert.AreEqual("ready", native.State);
        Assert.Contains("bounded-reading", native.Chunks.Single().Content);
        var retrieved = await factory.Services.GetRequiredService<KnowledgeRetrievalService>().SearchAsync(new(knowledge.Value.Name, ScopeRef: Owner), new() { Query = "bounded-reading" }, default);
        Assert.IsNotEmpty(retrieved.Items);
    }

    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(false, 3600)]
    [DataRow(true, 0)]
    [DataRow(true, 3600)]
    public async Task NativeKnowledgeProjectsTwoInputsThroughAwpIntoAnIndependentRetainedStorage(bool refresh, int maximumAgeSeconds)
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var management = factory.Services.GetRequiredService<DataSourceManagementService>();
        var first = await management.CreateAsync(Source("knowledge-alpha", fixture, JsonSerializer.SerializeToElement(new { dataset = "alpha" })), default);
        var second = await management.CreateAsync(Source("knowledge-beta", fixture, JsonSerializer.SerializeToElement(new { dataset = "beta" })), default);
        var resources = factory.Services.GetRequiredService<IResourceStore>();
        var original = (await resources.GetExactAsync<StorageResource>(fixture.Storage.Storage, default))!.Value;
        var output = await factory.Services.GetRequiredService<StorageManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = StorageResourceKinds.Storage,
            Metadata = new() { Name = "knowledge-output" },
            ScopeRef = Owner,
            Definition = original.Definition with { DisplayName = "Independent Knowledge output" }
        }, default);
        var outputReference = new StorageRevisionReference(new(Owner, output.Value.Namespace, output.Value.Kind, output.Value.Name),
            output.Value.Uid, output.Value.Generation, StorageDigests.Binding(output.Value.Definition));
        var transformedBytes = Encoding.UTF8.GetBytes("{\"dataset\":\"normalized-alpha\"}");
        var transform = await PublishAsync(factory.Services, "knowledge-normalize", KnowledgeFlowContracts.ArtifactTransformation,
            JsonSerializer.SerializeToElement(new
            {
                artifacts = new[] { new { artifactId = "normalized-alpha", kind = "inline",
                disposition = "publishable", mediaType = "application/json", contentBase64 = Convert.ToBase64String(transformedBytes) } }
            }));
        var sources = factory.Services.GetRequiredService<KnowledgeSourceManagementService>();
        var created = await sources.CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            Metadata = new() { Name = "native-retained" },
            ScopeRef = Owner,
            Definition = new()
            {
                DisplayName = "Native retained Knowledge",
                ProjectionFlow = new() { Name = "knowledge-projection-builtin", ScopeRef = Owner },
                RetrievalFlow = new() { Name = "knowledge-retrieval-builtin", ScopeRef = Owner },
                Output = new(outputReference, new(Write: true)),
                DataSources = [new() { Name = "alpha", DataSource = new(first.Value.Name, Owner),
                    RefreshIfNecessary = refresh, MaximumAge = TimeSpan.FromSeconds(maximumAgeSeconds),
                    TransformationFlow = new() { Name = transform.Flow.Name, ScopeRef = Owner, Version = transform.Version, UseActiveVersion = false } },
                    new() { Name = "beta", DataSource = new(second.Value.Name, Owner) }]
            }
        }, default);
        using var client = factory.CreateClient();
        SelectConsoleContext(client, Owner);
        var initialAcquisition = await AcquireAsync(client, first.Value.Name);
        _ = await AcquireAsync(client, second.Value.Name);
        Assert.IsNotNull(initialAcquisition.Manifest);
        var acquiredId = initialAcquisition.Manifest.Artifacts.Single().ArtifactId;
        var acquiredArtifact = await client.GetFromJsonAsync<FlowRunArtifactResource>($"/api/artifacts/flow-run-artifacts/{acquiredId}");
        Assert.IsNotNull(acquiredArtifact);
        Assert.AreEqual(first.Value.Name, acquiredArtifact.ProducerResource!.Value.Name);
        Assert.AreEqual(initialAcquisition.FlowRunId, acquiredArtifact.ProducerFlowRunId);
        Assert.AreEqual(initialAcquisition.Name, acquiredArtifact.ProducerExecutionId);
        Assert.IsNull(acquiredArtifact.SourceArtifactId);
        Assert.AreEqual(fixture.Storage.StorageUid, acquiredArtifact.Receipt.Materialization!.Target.Storage.StorageUid);
        Assert.AreEqual(initialAcquisition.Content!.Key, acquiredArtifact.Receipt.Materialization.Key);
        var durableStore = factory.Services.GetRequiredService<IArtifactDurableStore>();
        await Assert.ThrowsAsync<ArtifactValidationException>(() => durableStore.ReadAsync(new(Guid.NewGuid()),
            acquiredArtifact.Receipt.OpaqueReference, 0, 4, default));
        await Assert.ThrowsAsync<ArtifactValidationException>(() => durableStore.ResetAsync(new(fixture.Context.WorkspaceId),
            acquiredArtifact.Receipt.OpaqueReference, default));
        var substituted = ArtifactStorageReferences.Encode(acquiredArtifact.Receipt.Materialization with { Sha256 = new string('0', 64) });
        await Assert.ThrowsAsync<ArtifactValidationException>(() => durableStore.ReadAsync(new(fixture.Context.WorkspaceId), substituted, 0, 4, default));
        var projections = factory.Services.GetRequiredService<KnowledgeProjectionService>();
        var request = new StartKnowledgeProjectionRequest { IdempotencyKey = "native-integration" };
        var overlapping = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => projections.StartAsync(new(created.Value.Name, ScopeRef: Owner), request, default)));
        Assert.AreEqual(1, overlapping.Select(value => value.Value.Uid).Distinct().Count());
        var completed = overlapping[0];
        Assert.AreEqual(KnowledgeProjectionState.Succeeded, completed.Value.State, completed.Value.ErrorMessage);
        Assert.HasCount(2, completed.Value.Inputs);
        Assert.IsNotNull(completed.Value.Inputs[0].StorageInput);
        Assert.AreEqual(completed.Value.Inputs[0].AcquiredArtifacts[0].Digest, completed.Value.Inputs[0].StorageInput!.Sha256);
        Assert.AreEqual(refresh && maximumAgeSeconds == 0, completed.Value.Inputs[0].AcquisitionId != initialAcquisition.Name);
        Assert.IsNotNull(completed.Value.Inputs[0].TransformationFlowRunId);
        var snapshots = factory.Services.GetRequiredService<KnowledgeSnapshotService>();
        var activeView = (await snapshots.GetActiveAsync(new(created.Value.Name, ScopeRef: Owner), default))!;
        Assert.AreEqual(KnowledgeSnapshotLifecycleState.Active, activeView.LifecycleState);
        var published = activeView.Snapshot;
        var listedSnapshots = await client.GetFromJsonAsync<KnowledgeSnapshotView[]>(
            $"/api/knowledgesources/{created.Value.Name}/snapshots?scopeRef=/instance");
        Assert.AreEqual(KnowledgeSnapshotLifecycleState.Active, listedSnapshots!.Single().LifecycleState);
        Assert.AreEqual(KnowledgeSnapshotLifecycleState.Active,
            (await snapshots.GetAsync(new(published.Name, published.Namespace), default)).LifecycleState);
        Assert.HasCount(2, published.Artifacts);
        Assert.IsTrue(published.Artifacts.All(item => item.Content?.Target.Storage.StorageUid == output.Value.Uid));
        Assert.AreNotEqual(fixture.Storage.StorageUid, published.Artifacts[0].Content!.Target.Storage.StorageUid);
        Assert.IsNotNull(published.Artifacts[0].Content!.Binding);
        Assert.AreEqual(original.Definition.Type.TypeUid, published.Artifacts[0].Content!.Binding!.Type.TypeUid);
        Assert.AreEqual("storage.local.retained-publication/v1", published.Artifacts[0].Content!.Binding!.PublicationContract);
        var catalog = await client.GetFromJsonAsync<FlowRunArtifactResource[]>("/api/artifacts/flow-run-artifacts");
        Assert.IsNotNull(catalog);
        foreach (var entry in published.Artifacts)
        {
            var record = catalog.Single(item => item.ArtifactId.ToString() == entry.ArtifactId);
            Assert.AreEqual(created.Value.Name, record.ProducerResource!.Value.Name);
            Assert.AreEqual(completed.Value.ProjectionFlowRunId, record.ProducerFlowRunId);
            Assert.AreEqual(completed.Value.Name, record.ProducerExecutionId);
            Assert.AreEqual(output.Value.Uid, record.Receipt.Materialization!.Target.Storage.StorageUid);
            Assert.AreEqual(entry.Content, record.Receipt.Materialization.Publication);
        }
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(fixture.Context))
        {
            var preview = await factory.Services.GetRequiredService<DurableArtifactContentReader>().ReadAsync(
                FlowRunArtifactId.Parse(published.Artifacts[0].ArtifactId), null, 0, 65536, "snapshot-artifact-preview", default);
            Assert.Contains("normalized-alpha", preview.Text);
        }
        var publications = factory.Services.GetRequiredService<IStoragePublicationGateway>();
        var bytes = await publications.ReadAsync(published.Artifacts[0].Content!, 0, 65536, default);
        Assert.AreEqual(Encoding.UTF8.GetString(transformedBytes), Encoding.UTF8.GetString(bytes.Span));
        var retry = await projections.StartAsync(new(created.Value.Name, ScopeRef: Owner), request, default);
        Assert.AreEqual(completed.Value.Uid, retry.Value.Uid);
        var retrieved = await factory.Services.GetRequiredService<KnowledgeRetrievalService>().SearchAsync(
            new(created.Value.Name, ScopeRef: Owner), new() { Query = "normalized-alpha" }, default);
        Assert.AreEqual(published.Uid, retrieved.SnapshotUid);
        Assert.IsNotEmpty(retrieved.Items);
        var next = await projections.StartAsync(new(created.Value.Name, ScopeRef: Owner), new() { IdempotencyKey = "next-publication" }, default);
        Assert.AreNotEqual(published.Name, next.Value.SnapshotName);
        Assert.AreEqual(Encoding.UTF8.GetString(transformedBytes), Encoding.UTF8.GetString((await publications.ReadAsync(published.Artifacts[0].Content!, 0, 65536, default)).Span));
        await Assert.ThrowsAsync<KnowledgeProjectionException>(() => projections.StartAsync(new(created.Value.Name, ScopeRef: Owner),
            request with { Parameters = JsonSerializer.SerializeToElement(new { changed = true }) }, default));
        var active = (await snapshots.GetActiveAsync(new(created.Value.Name, ScopeRef: Owner), default))!.Snapshot;
        Assert.AreEqual(next.Value.SnapshotName, active.Name);
        await Assert.ThrowsAsync<StorageException>(() => publications.PrepareAsync(published.Artifacts[0].Content!.Target,
            completed.Value.Name + ":normalized-alpha", Encoding.UTF8.GetBytes("changed"), default));
        var shared = await sources.CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            Metadata = new() { Name = "shared-input-view" },
            ScopeRef = Owner,
            Definition = created.Value.Definition with { DisplayName = "Another view", DataSources = [new() { Name = "shared", DataSource = new(first.Value.Name, Owner) }] }
        }, default);
        var alternate = await projections.StartAsync(new(shared.Value.Name, ScopeRef: Owner), new() { IdempotencyKey = "shared-view" }, default);
        Assert.AreNotEqual(next.Value.SnapshotName, alternate.Value.SnapshotName);
        Assert.AreEqual(next.Value.Inputs[0].AcquisitionId, alternate.Value.Inputs[0].AcquisitionId);
        await Assert.ThrowsAsync<KnowledgeSnapshotException>(() => snapshots.ResolveForRetrievalAsync(new(shared.Value.Name, ScopeRef: Owner), published.Name, default));
        var forged = published.Artifacts[0].Content! with { Version = new string('0', 64) };
        await Assert.ThrowsAsync<StorageException>(() => publications.ReadAsync(forged, 0, 100, default));
        var brokenFlow = await PublishAsync(factory.Services, "knowledge-empty-projection", KnowledgeFlowContracts.Projection,
            JsonSerializer.SerializeToElement(new { artifacts = Array.Empty<object>() }));
        var changed = await sources.PutExactAsync(Owner, new(created.Value.Name), created.Value.Definition with
        { ProjectionFlow = new() { Name = brokenFlow.Flow.Name, ScopeRef = Owner, Version = brokenFlow.Version, UseActiveVersion = false } }, created.ETag, default);
        await Assert.ThrowsAsync<KnowledgeProjectionException>(() => projections.StartAsync(new(created.Value.Name, ScopeRef: Owner), new() { IdempotencyKey = "failure" }, default));
        Assert.AreEqual(next.Value.SnapshotName, (await snapshots.GetActiveAsync(new(created.Value.Name, ScopeRef: Owner), default))!.Snapshot.Name);
        _ = await sources.SetEnabledAsync(new(created.Value.Name, ScopeRef: Owner), false, changed.ETag, default);
        await Assert.ThrowsAsync<KnowledgeRetrievalException>(() => factory.Services.GetRequiredService<KnowledgeRetrievalService>()
            .SearchAsync(new(created.Value.Name, ScopeRef: Owner), new() { Query = "alpha" }, default));
        var revisedInput = await management.PutAsync(first.Value.Namespace, first.Value.Name, Owner,
            first.Value.Definition with { DisplayName = "Revised input" }, first.ETag, default);
        await Assert.ThrowsAsync<KnowledgeSourceValidationException>(() => sources.PutExactAsync(Owner, new(shared.Value.Name),
            shared.Value.Definition with { DisplayName = "Cosmetic edit" }, shared.ETag, default));
        var explicitlyRebound = await sources.PutExactAsync(Owner, new(shared.Value.Name), shared.Value.Definition,
            shared.ETag, default, rebindDependencies: true);
        Assert.AreEqual(revisedInput.Value.Generation, explicitlyRebound.Value.Composition!.DataSources.Single().DataSourceGeneration);
        var renamed = await factory.Services.GetRequiredService<StorageManagementService>().PutExactAsync(Owner, output.Value.Namespace,
            output.Value.Name, output.Value.Definition with { DisplayName = "Edited Storage" }, output.ETag, default);
        Assert.AreEqual(output.Value.Uid, renamed.Value.Uid);
        var stale = await Assert.ThrowsAsync<StorageException>(() => publications.ReadAsync(published.Artifacts[0].Content!, 0, 100, default));
        Assert.AreEqual("storage_revision_stale", stale.Code);
        var restricted = await factory.Services.GetRequiredService<StorageManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = StorageResourceKinds.Storage,
            Metadata = new() { Name = "restricted-publications" },
            ScopeRef = Owner,
            Definition = original.Definition with { Envelope = JsonSerializer.SerializeToElement(new { list = true, stat = true, read = true, write = true, prefix = "allowed/", keyFrom = "allowed/z" }) }
        }, default);
        var restrictedRef = new StorageRevisionReference(new(Owner, restricted.Value.Namespace, restricted.Value.Kind, restricted.Value.Name),
            restricted.Value.Uid, restricted.Value.Generation, StorageDigests.Binding(restricted.Value.Definition));
        var rangeFailure = await Assert.ThrowsAsync<StorageException>(() => publications.PrepareAsync(
            new(restrictedRef, new(Owner, created.Value.Address), new(Write: true, Prefix: "allowed/")), "outside-key-range", transformedBytes, default));
        Assert.AreEqual("storage_publication_forbidden", rangeFailure.Code);
    }

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

        // Existing exact version receipts remain readable when the provider exposes no checksum.
        var versionOnly = await factory.Services.GetRequiredService<DataSourceDatasetAcquisitionService>().ReadAsync(
            acquisition with { Content = acquisition.Content with { Version = "current-version", Sha256 = null } }, 0, 4, default);
        CollectionAssert.AreEqual(gateway.Bytes[..3], Convert.FromBase64String(versionOnly.ContentBase64));

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
            if (maximumBytes > 4) throw new Agentstration.Storage.StorageException("storage_content_limit_exceeded", "Fixture read bound.");
            var length = Math.Min(3, Math.Min(maximumBytes, Bytes.Length - (int)offset));
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
    public async Task UnavailableHistoricalContentDoesNotBlockHistoryOrANewAcquisition()
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var fixture = await PrepareAsync(factory.Services);
        using var scope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(fixture.Context);
        var source = await factory.Services.GetRequiredService<DataSourceManagementService>()
            .CreateAsync(Source("repair-history", fixture, JsonSerializer.SerializeToElement(new { dataset = "new-content" })), default);
        using var client = factory.CreateClient();
        SelectConsoleContext(client, Owner);
        var acquisition = await AcquireAsync(client, source.Value.Name);
        var resources = factory.Services.GetRequiredService<IResourceStore>();
        DataSourceAcquisitionResource historical;
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(fixture.Context))
        {
            var stored = (await resources.GetExactAsync<DataSourceAcquisitionResource>(new(acquisition.ScopeRef!.Value,
                acquisition.Namespace, acquisition.Kind, acquisition.Name), default))!;
            // A pre-upgrade history still has its Artifact, but this exact native content
            // version is unavailable. Repair is metadata upkeep, not a prerequisite to use.
            historical = stored.Value with { Manifest = null, Content = stored.Value.Content! with { Version = "unavailable" } };
            historical = (await resources.PutExactAsync(historical.ScopeRef!.Value, historical, stored.ETag, false, default)).Value;
        }
        using var history = await client.GetAsync($"/api/datasources/{source.Value.Name}/acquisitions?scopeRef=/instance");
        Assert.AreEqual(HttpStatusCode.OK, history.StatusCode, await history.Content.ReadAsStringAsync());
        using var missing = await client.GetAsync($"/api/datasourceacquisitions/{acquisition.Name}/content");
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Contains("data_source_content_version_unavailable", await missing.Content.ReadAsStringAsync());
        var fresh = await AcquireAsync(client, source.Value.Name);
        Assert.AreNotEqual(acquisition.Name, fresh.Name);
        Assert.AreEqual("new-content", await ReadDatasetAsync(client, fresh.Name));
        using (factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(fixture.Context))
        {
            var preserved = (await resources.GetExactAsync<DataSourceAcquisitionResource>(new(historical.ScopeRef!.Value,
                historical.Namespace, historical.Kind, historical.Name), default))!.Value;
            Assert.AreEqual(JsonSerializer.Serialize(historical), JsonSerializer.Serialize(preserved));
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
        KnowledgeProjectionResource retainedProjection = null!;
        KnowledgeSnapshotResource retainedSnapshot = null!;
        KnowledgeSourceResource retainedKnowledge = null!;
        try
        {
            await using (var first = Factory().WithWebHostBuilder(builder => builder.UseEnvironment("Development").UseSetting("Agentstration:Authentication:Mode", "Development")
                .UseSetting("Data:Directory", directory).UseSetting("ConnectionStrings:Identity", "Data Source=" + Path.Combine(directory, "identity.db"))
                .UseSetting("Agentstration:Authentication:DataProtectionKeysPath", Path.Combine(directory, "keys"))
                .UseSetting("Data:ControlPlanePath", Path.Combine(directory, "control-plane.db"))
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
                .UseSetting("Data:Directory", directory).UseSetting("ConnectionStrings:Identity", "Data Source=" + Path.Combine(directory, "identity.db"))
                .UseSetting("Agentstration:Authentication:DataProtectionKeysPath", Path.Combine(directory, "keys"))
                .UseSetting("Data:ControlPlanePath", Path.Combine(directory, "control-plane.db"))
                .UseSetting("Data:WorkPlanePath", Path.Combine(directory, "work-plane.db")).UseSetting("Data:FlowPath", Path.Combine(directory, "flow-plane.db"))
                .UseSetting("Data:RuntimePath", Path.Combine(directory, "runtime-plane.db"))).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
                {
                    services.AddSingleton(provider => new SealedOutputInterruption(ActivatorUtilities.CreateInstance<KnowledgeOutputPublisher>(provider)));
                    services.Replace(ServiceDescriptor.Singleton<IKnowledgeOutputPublisher>(provider => provider.GetRequiredService<SealedOutputInterruption>()));
                })))
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
                Assert.AreEqual(fixture.Storage.StorageUid, completed.Dataset!.Definition.Storage!.StorageUid);
                Assert.AreEqual(fixture.Operation.Uid, completed.Dataset.Operation.Uid);
                retainedKnowledge = (await second.Services.GetRequiredService<KnowledgeSourceManagementService>().CreateAsync(new()
                {
                    ApiVersion = ResourceApiVersions.CoreV1,
                    Kind = KnowledgeResourceKinds.KnowledgeSource,
                    Metadata = new() { Name = "restart-knowledge" },
                    ScopeRef = Owner,
                    Definition = new()
                    {
                        DisplayName = "Restart Knowledge",
                        Output = new(fixture.Storage, new(Write: true)),
                        DataSources = [new() { Name = "retained", DataSource = new(source.Name, Owner) }],
                        ProjectionFlow = new() { Name = "knowledge-projection-builtin", ScopeRef = Owner },
                        RetrievalFlow = new() { Name = "knowledge-retrieval-builtin", ScopeRef = Owner }
                    }
                }, default)).Value;
                var interrupted = await Assert.ThrowsAsync<KnowledgeProjectionException>(() => second.Services.GetRequiredService<KnowledgeProjectionService>().StartAsync(
                    new(retainedKnowledge.Name, ScopeRef: Owner), new() { IdempotencyKey = "restart-publication" }, default));
                Assert.AreEqual("knowledge_projection_publication_pending", interrupted.Code);
                Assert.IsNull(await second.Services.GetRequiredService<KnowledgeSnapshotService>().GetActiveAsync(new(retainedKnowledge.Name, ScopeRef: Owner), default));
                retainedProjection = (await second.Services.GetRequiredService<KnowledgeProjectionService>().StartAsync(
                    new(retainedKnowledge.Name, ScopeRef: Owner), new() { IdempotencyKey = "restart-publication" }, default)).Value;
                retainedSnapshot = (await second.Services.GetRequiredService<KnowledgeSnapshotService>().GetActiveAsync(
                    new(retainedKnowledge.Name, ScopeRef: Owner), default))!.Snapshot;
                Assert.AreEqual(second.Services.GetRequiredService<SealedOutputInterruption>().SealedContent![0].Content, retainedSnapshot.Artifacts[0].Content);
                // Inject an interrupted final activation/completion, then replace the actual host.
                using (scopes.Push(fixture.Context))
                {
                    var store = second.Services.GetRequiredService<IResourceStore>();
                    var persisted = (await store.GetExactAsync<KnowledgeProjectionResource>(new(retainedProjection.ScopeRef!.Value,
                        retainedProjection.Namespace, retainedProjection.Kind, retainedProjection.Name), default))!;
                    _ = await store.PutExactAsync(persisted.Value.ScopeRef!.Value, persisted.Value with
                    { Generation = persisted.Value.Generation + 1, State = KnowledgeProjectionState.Running }, persisted.ETag, false, default);
                    var observed = (await store.GetExactAsync<KnowledgeSnapshotObservedResource>(new(retainedProjection.ScopeRef.Value,
                        retainedKnowledge.Namespace, KnowledgeResourceKinds.KnowledgeSnapshotObservedState, "snapshot-state-" + retainedKnowledge.Uid.ToString("N")), default))!;
                    _ = await store.PutExactAsync(observed.Value.ScopeRef!.Value, observed.Value with
                    { Generation = observed.Value.Generation + 1, ActiveSnapshotName = null, ActiveSnapshotUid = null }, observed.ETag, false, default);
                }
                var content = await second.Services.GetRequiredService<DataSourceDatasetAcquisitionService>().ReadAsync(completed, 0, 65536, default);
                Assert.Contains("retained", Encoding.UTF8.GetString(Convert.FromBase64String(content.ContentBase64)));

            }
            // A second restart recovers publication, then retrieves without another origin execution.
            await using (var third = Factory().WithWebHostBuilder(builder => builder.UseEnvironment("Development").UseSetting("Agentstration:Authentication:Mode", "Development")
                .UseSetting("Data:Directory", directory).UseSetting("ConnectionStrings:Identity", "Data Source=" + Path.Combine(directory, "identity.db"))
                .UseSetting("Agentstration:Authentication:DataProtectionKeysPath", Path.Combine(directory, "keys"))
                .UseSetting("Data:ControlPlanePath", Path.Combine(directory, "control-plane.db"))
                .UseSetting("Data:WorkPlanePath", Path.Combine(directory, "work-plane.db")).UseSetting("Data:FlowPath", Path.Combine(directory, "flow-plane.db"))
                .UseSetting("Data:RuntimePath", Path.Combine(directory, "runtime-plane.db"))))
            {
                _ = await GetBootstrapContextAsync(third);
                var scopes = third.Services.GetRequiredService<IRequestContextScopeFactory>();
                await using var worker = await AwpTestWorker.StartAsync(third);
                using (scopes.PushSystem()) await third.Services.GetRequiredService<KnowledgeProjectionService>().RecoverPendingAsync(default);
                using var caller = scopes.PushInstance(fixture.Context);
                var recovered = await third.Services.GetRequiredService<KnowledgeProjectionService>().GetAsync(retainedProjection.Name, retainedProjection.Namespace, default);
                Assert.AreEqual(KnowledgeProjectionState.Succeeded, recovered.Value.State, recovered.Value.ErrorMessage);
                Assert.AreEqual(retainedProjection.Uid, recovered.Value.Uid);
                var active = (await third.Services.GetRequiredService<KnowledgeSnapshotService>().GetActiveAsync(new(retainedKnowledge.Name, ScopeRef: Owner), default))!.Snapshot;
                Assert.AreEqual(retainedSnapshot.Uid, active.Uid);
                Assert.AreEqual(retainedSnapshot.Artifacts[0].Content, active.Artifacts[0].Content);
                var retrieved = await third.Services.GetRequiredService<KnowledgeRetrievalService>().SearchAsync(new(retainedKnowledge.Name, ScopeRef: Owner), new() { Query = "retained" }, default);
                Assert.IsNotEmpty(retrieved.Items);
                var acquisitions = third.Services.GetRequiredService<DataSourceAcquisitionService>();
                var history = await acquisitions.ListAsync(source.Namespace, source.Name, Owner, default);
                Assert.HasCount(1, history);
                Assert.AreEqual(accepted.Uid, history[0].Uid);
                var content = await third.Services.GetRequiredService<DataSourceDatasetAcquisitionService>().ReadAsync(history[0], 0, 65536, default);
                Assert.Contains("retained", Encoding.UTF8.GetString(Convert.FromBase64String(content.ContentBase64)));
                // Simulate pre-upgrade native history: bytes and Snapshot exist, Artifact metadata is absent.
                var resourceStore = third.Services.GetRequiredService<IResourceStore>();
                var snapshotBefore = JsonSerializer.Serialize(active);
                var contentBefore = history[0].Content;
                var acquisitionArtifactId = history[0].Manifest!.Artifacts.Single().ArtifactId;
                using (scopes.Push(fixture.Context))
                {
                    var acquisitionRecord = (await resourceStore.GetExactAsync<DataSourceAcquisitionResource>(new(history[0].ScopeRef!.Value,
                        history[0].Namespace, history[0].Kind, history[0].Name), default))!;
                    _ = await resourceStore.PutExactAsync(acquisitionRecord.Value.ScopeRef!.Value, acquisitionRecord.Value with
                    { Generation = acquisitionRecord.Value.Generation + 1, Manifest = null }, acquisitionRecord.ETag, false, default);
                    foreach (var id in new[] { acquisitionArtifactId, active.Artifacts[0].ArtifactId })
                        await resourceStore.DeleteExactAsync(new(history[0].ScopeRef!.Value, ResourceNamespace.Default,
                            ArtifactResourceKinds.FlowRunArtifact, id), null, default);
                }
                await third.Services.GetRequiredService<StoredArtifactReconciliationService>().ReconcileAsync(default);
                await third.Services.GetRequiredService<StoredArtifactReconciliationService>().ReconcileAsync(default);
                var reconciled = await acquisitions.ListAsync(source.Namespace, source.Name, Owner, default);
                Assert.HasCount(1, reconciled);
                Assert.AreEqual(contentBefore, reconciled[0].Content);
                Assert.AreEqual(acquisitionArtifactId, reconciled[0].Manifest!.Artifacts.Single().ArtifactId);
                Assert.AreEqual(history[0].CompletedAt, reconciled[0].CompletedAt);
                var after = (await third.Services.GetRequiredService<KnowledgeSnapshotService>().GetActiveAsync(
                    new(retainedKnowledge.Name, ScopeRef: Owner), default))!.Snapshot;
                Assert.AreEqual(snapshotBefore, JsonSerializer.Serialize(after));
                using (scopes.Push(fixture.Context))
                {
                    var catalog = await third.Services.GetRequiredService<ArtifactManagementService>().ListFlowRunArtifactsAsync(default);
                    Assert.AreEqual(1, catalog.Count(item => item.Value.ArtifactId.ToString() == acquisitionArtifactId));
                    Assert.AreEqual(1, catalog.Count(item => item.Value.ArtifactId.ToString() == active.Artifacts[0].ArtifactId));
                }

            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class SealedOutputInterruption(KnowledgeOutputPublisher inner) : IKnowledgeOutputPublisher
    {
        public IReadOnlyList<KnowledgeSnapshotArtifact>? SealedContent { get; private set; }
        public async Task<IReadOnlyList<KnowledgeSnapshotArtifact>> PublishAsync(KnowledgeSourceResource source,
            KnowledgeProjectionResource projection, KnowledgeProjectionManifest manifest, CancellationToken cancellationToken)
        {
            var result = await inner.PublishAsync(source, projection, manifest, cancellationToken);
            if (SealedContent is null)
            {
                SealedContent = result;
                throw new KnowledgeProjectionException("knowledge_projection_publication_pending", "Injected interruption after actual output sealing.");
            }
            return result;
        }
    }

    private static async Task<Fixture> PrepareAsync(IServiceProvider services, bool emptyInput = false, JsonElement? operationResult = null, bool declareAcquisitionOutput = true, JsonElement? operationOutputSchema = null)
    {
        var bootstrap = await services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        await services.GetRequiredService<IWorkspacePlatformResourceProvisioner>().EnsureAllAsync(default);
        var workspace = await services.GetRequiredService<IOperationalTopologyProvisioner>().GetInstanceOperationsWorkspaceAsync(default);
        var context = new RequestContext(bootstrap.PrincipalId, workspace.TenantId, workspace.Id);
        using var scope = services.GetRequiredService<IRequestContextScopeFactory>().PushInstance(context);
        var input = emptyInput ? JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false })
            : JsonSerializer.SerializeToElement(new { type = "object", properties = new { dataset = new { type = "string", minLength = 1 } }, required = new[] { "dataset" }, additionalProperties = false });
        var flow = await PublishAsync(services, "fixture-acquire", DataSourceDatasetCompositionService.FlowContract,
            operationResult ?? (emptyInput ? Empty : JsonSerializer.SerializeToElement(new { dataset = "${input.parameters.dataset}" })), declareAcquisitionOutput);
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
                Definition = JsonSerializer.SerializeToElement(new { readOnly = true, inputSchema = input, outputSchema = operationOutputSchema ?? (operationResult is null ? input : JsonSerializer.SerializeToElement(new { type = "object" })) }) }]
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

    private static async Task<ConnectedServiceDiscoveryFlowReference> PublishAsync(IServiceProvider services, string name, string contract, JsonElement output, bool declareOutput = true)
    {
        var schema = contract == KnowledgeFlowContracts.ArtifactTransformation
            ? JsonSerializer.SerializeToElement(new { type = "object", properties = new { bindingName = new { type = "string" }, dataSource = new { type = "object" }, artifacts = new { type = "array" }, configuration = new { type = "object" }, caller = new { type = "object" }, correlationId = new { type = "string" }, transformationId = new { type = "string" } }, required = new[] { "bindingName", "dataSource", "artifacts", "configuration", "caller", "correlationId", "transformationId" } })
            : JsonSerializer.SerializeToElement(new { type = "object" });
        if (contract == KnowledgeFlowContracts.Projection)
            schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { knowledgeSourceId = new { type = "string" }, knowledgeSourceUid = new { type = "string" }, knowledgeSourceGeneration = new { type = "integer" }, inputs = new { type = "array" }, artifacts = new { type = "array" }, parameters = new { type = "object" }, caller = new { type = "object" }, correlationId = new { type = "string" }, projectionId = new { type = "string" } }, required = new[] { "knowledgeSourceId", "knowledgeSourceUid", "knowledgeSourceGeneration", "inputs", "artifacts", "parameters", "caller", "correlationId", "projectionId" } });
        var outputSchema = contract is KnowledgeFlowContracts.ArtifactTransformation or KnowledgeFlowContracts.Projection
            ? JsonSerializer.SerializeToElement(new { type = "object", properties = new { artifacts = new { type = "array" } }, required = new[] { "artifacts" } }) : schema;
        var flows = services.GetRequiredService<FlowService>();
        var flow = await flows.CreateExactAsync(Owner, new CreateFlowCommand(name, null, "1.0.0", true,
            new WorkflowFlowDefinition("input", [new FlowNode("input", FlowNodeKind.Input)], []),
            new Dictionary<string, string> { [FlowMetadataKeys.Contract] = contract }, new FlowGraphDefinition
            {
                EntryStep = "input",
                InputSchema = schema,
                OutputSchema = declareOutput ? outputSchema : null,
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
