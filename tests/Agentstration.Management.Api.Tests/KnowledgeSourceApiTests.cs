using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Artifacts;
using Agentstration.Artifacts.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Artifacts;
using Agentstration.Infrastructure.Knowledge;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Security.Contracts;
using Agentstration.Tools;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class KnowledgeSourceApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    public async Task BuiltInKnowledgeFlowsAreProvisionedAndExecuteThroughGovernedToolSetRoutes()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var workspaceId = new WorkspaceId(context.WorkspaceId);
        var flows = factory.Services.GetRequiredService<FlowService>();
        var ingestionFlow = await flows.GetAsync(workspaceId, new(KnowledgePlatformResourceProvisioner.IngestionFlowName), default);
        var retrievalFlow = await flows.GetAsync(workspaceId, new(KnowledgePlatformResourceProvisioner.RetrievalFlowName), default);
        Assert.IsNotNull(ingestionFlow);
        Assert.IsNotNull(retrievalFlow);
        Assert.AreEqual(KnowledgePlatformResourceProvisioner.Version, ingestionFlow.Value.ActiveVersion);
        Assert.AreEqual(KnowledgeFlowContracts.Ingestion, ingestionFlow.Value.Metadata[KnowledgeFlowContracts.MetadataKey]);
        Assert.AreEqual(KnowledgePlatformResourceProvisioner.Version, retrievalFlow.Value.ActiveVersion);
        Assert.AreEqual(KnowledgeFlowContracts.Retrieval, retrievalFlow.Value.Metadata[KnowledgeFlowContracts.MetadataKey]);
        Assert.AreEqual("true", ingestionFlow.Value.Metadata[ResourceProvenanceAnnotations.BuiltIn]);
        Assert.AreEqual("true", retrievalFlow.Value.Metadata[ResourceProvenanceAnnotations.BuiltIn]);
        Assert.IsTrue(ingestionFlow.Value.Graph!.Steps.OfType<ToolRouteFlowStepDefinition>().Any());
        Assert.HasCount(3, retrievalFlow.Value.Graph!.Steps.OfType<ToolRouteFlowStepDefinition>().ToArray());

        var store = factory.Services.GetRequiredService<IResourceStore>();
        var scope = ResourceScopeRef.Workspace(context.WorkspaceId);
        var ingestionTools = await store.GetExactAsync<ToolSetResource>(ScopedResourceAddress.Create(scope,
            ResourceNamespace.Default, ToolResourceKinds.ToolSet, KnowledgePlatformResourceProvisioner.IngestionToolSetName), default);
        var retrievalTools = await store.GetExactAsync<ToolSetResource>(ScopedResourceAddress.Create(scope,
            ResourceNamespace.Default, ToolResourceKinds.ToolSet, KnowledgePlatformResourceProvisioner.RetrievalToolSetName), default);
        Assert.AreEqual("true", ingestionTools?.Value.Metadata.Annotations[ResourceProvenanceAnnotations.BuiltIn]);
        Assert.AreEqual("true", retrievalTools?.Value.Metadata.Annotations[ResourceProvenanceAnnotations.BuiltIn]);

        var artifacts = factory.Services.GetRequiredService<ArtifactManagementService>();
        var staged = await artifacts.CreateStagedAsync(new("builtin-knowledge.md", "text/markdown", new ArtifactProducer
        {
            Kind = ArtifactProducerKind.FlowRun,
            Id = "builtin-producer",
            FlowRunId = "builtin-producer",
            FlowStepId = "output"
        }), default);
        var content = "Agentstration builtin retrieval is deterministic."u8.ToArray();
        _ = await artifacts.WriteAsync(staged.Value.ArtifactId, 0, content, default);
        _ = await artifacts.SealAsync(staged.Value.ArtifactId, default);
        var storedOutput = await factory.Services.GetRequiredService<ArtifactStorageWriteMcpTool>().ExecuteAsync(
            new(context.TenantId, workspaceId, context.PrincipalId, "builtin-storage-call", "builtin-correlation",
                JsonSerializer.SerializeToElement(new
                {
                    stagedArtifactId = staged.Value.ArtifactId.ToString(),
                    producerFlowRunId = "builtin-producer",
                    producerFlowStepId = "output"
                }), ToolDefinitionCallerKind.Flow, RunId: "builtin-storage-run", FlowStepId: "storage"), default);
        var durableId = FlowRunArtifactId.Parse(storedOutput!.Value.GetProperty("flowRunArtifactId").GetString()!);
        var durable = await artifacts.GetFlowRunArtifactAsync(durableId, default);
        Assert.IsNotNull(durable);

        var source = await factory.Services.GetRequiredService<KnowledgeSourceManagementService>().CreateAsync(
            new KnowledgeSourceResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = KnowledgeResourceKinds.KnowledgeSource,
                Metadata = new() { Name = "builtin-knowledge" },
                ScopeRef = scope,
                Definition = Properties("Built-in Knowledge", KnowledgePlatformResourceProvisioner.IngestionFlowName,
                    KnowledgePlatformResourceProvisioner.RetrievalFlowName)
            }, default);
        var acquisitions = factory.Services.GetRequiredService<KnowledgeAcquisitionService>();
        var started = await acquisitions.StartAsync(new("builtin-knowledge"),
            JsonSerializer.SerializeToElement(new { artifactIds = new[] { durableId.ToString() } }),
            "builtin-acquisition", "builtin-acquisition", default);
        await factory.Services.GetRequiredService<FlowRunService>().ExecuteAsync(new(started.Value.FlowRunId,
            new(context.TenantId, workspaceId, context.PrincipalId)), default);
        var completed = await acquisitions.GetAsync(started.Value.Name, ResourceNamespace.Default, default);
        Assert.AreEqual(KnowledgeAcquisitionState.Succeeded, completed.Value.State, completed.Value.ErrorMessage);
        Assert.AreEqual(durableId.ToString(), completed.Value.Manifest?.Artifacts.Single().ArtifactId);

        _ = await CreateActiveSnapshotAsync(factory.Services, context, source.Value, durable.Value, "builtin-snapshot");
        var retrieval = factory.Services.GetRequiredService<KnowledgeRetrievalService>();
        var result = await retrieval.SearchAsync(
            new("builtin-knowledge"), new SearchKnowledgeRequest { Query = "deterministic", Limit = 5 }, default);
        Assert.HasCount(1, result.Items);
        Assert.AreEqual(durableId.ToString(), result.Items[0].ArtifactId);
        StringAssert.Contains(result.Items[0].Content, "deterministic");
        Assert.AreEqual(KnowledgePlatformResourceProvisioner.RetrievalFlowName, result.RetrievalFlow.Name);

        var query = await retrieval.QueryAsync(new("builtin-knowledge"), new QueryKnowledgeRequest
        {
            Question = "Is retrieval deterministic?",
            MaximumItems = 5,
            MaximumOutputCharacters = 1_024
        }, default);
        Assert.HasCount(1, query.Items);
        StringAssert.Contains(query.Answer, "deterministic");

        var read = await retrieval.ReadAsync(new("builtin-knowledge"), new ReadKnowledgeRequest
        {
            ArtifactId = durableId.ToString(),
            Offset = 0,
            Length = content.Length
        }, default);
        Assert.HasCount(1, read.Items);
        Assert.AreEqual("Agentstration builtin retrieval is deterministic.", read.Items[0].Content);
    }

    [TestMethod]
    public async Task AcquisitionRunsTheExactPublishedIngestionFlowAndReturnsABoundedManifest()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var staged = await factory.Services.GetRequiredService<ArtifactManagementService>().CreateStagedAsync(
            new("docs.md", "text/markdown", new ArtifactProducer
            {
                Kind = ArtifactProducerKind.FlowRun,
                Id = "knowledge-test",
                FlowRunId = "knowledge-test"
            }), default);
        _ = await factory.Services.GetRequiredService<ArtifactManagementService>().WriteAsync(
            staged.Value.ArtifactId, 0, "# Docs"u8.ToArray(), default);
        _ = await factory.Services.GetRequiredService<ArtifactManagementService>().SealAsync(staged.Value.ArtifactId, default);
        await CreatePublishedIngestionFlowAsync(factory.Services, context, "docs-ingest", staged.Value.ArtifactId.ToString());
        await CreatePublishedFlowAsync(factory.Services, context, "docs-retrieve");
        var source = await factory.Services.GetRequiredService<KnowledgeSourceManagementService>().CreateAsync(
            new KnowledgeSourceResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = KnowledgeResourceKinds.KnowledgeSource,
                Metadata = new() { Name = "docs" },
                ScopeRef = ResourceScopeRef.Workspace(context.WorkspaceId),
                Definition = Properties("Documentation", "docs-ingest", "docs-retrieve")
            }, default);
        using var client = factory.CreateClient();
        using var firstRequest = new HttpRequestMessage(HttpMethod.Post, "/api/knowledgesources/docs/acquisitions")
        {
            Content = JsonContent.Create(new StartKnowledgeAcquisitionRequest
            {
                Parameters = JsonSerializer.SerializeToElement(new { locale = "en-US" }),
                CorrelationId = "knowledge-correlation"
            })
        };
        firstRequest.Headers.Add("Idempotency-Key", "docs-acquisition-1");

        using var startedResponse = await client.SendAsync(firstRequest);

        Assert.AreEqual(HttpStatusCode.Accepted, startedResponse.StatusCode,
            await startedResponse.Content.ReadAsStringAsync());
        var started = await startedResponse.Content.ReadFromJsonAsync<KnowledgeAcquisitionResource>();
        Assert.IsNotNull(started);
        Assert.AreEqual(source.Value.Uid, started.KnowledgeSourceUid);
        Assert.AreEqual(source.Value.Generation, started.KnowledgeSourceGeneration);
        Assert.AreEqual("1.0.0", started.IngestionFlow.Version);
        Assert.AreEqual(KnowledgeFlowContracts.Ingestion, started.IngestionFlow.Contract);
        Assert.AreEqual("knowledge-correlation", started.CorrelationId);

        await factory.Services.GetRequiredService<FlowRunService>().ExecuteAsync(
            new FlowRunQueueItem(started.FlowRunId,
                new FlowRunScope(context.TenantId, new WorkspaceId(context.WorkspaceId), context.PrincipalId)), default);

        KnowledgeAcquisitionResource? completed = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            completed = await client.GetFromJsonAsync<KnowledgeAcquisitionResource>(startedResponse.Headers.Location);
            if (completed?.State is KnowledgeAcquisitionState.Succeeded or KnowledgeAcquisitionState.Failed) break;
            await Task.Delay(50);
        }
        Assert.IsNotNull(completed);
        Assert.AreEqual(KnowledgeAcquisitionState.Succeeded, completed.State, completed.ErrorMessage);
        Assert.HasCount(1, completed.Manifest!.Artifacts);
        Assert.AreEqual(KnowledgeArtifactDisposition.Publishable, completed.Manifest.Artifacts[0].Disposition);
        Assert.AreEqual(staged.Value.ArtifactId.ToString(), completed.Manifest.Artifacts[0].ArtifactId);

        using var repeatedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/knowledgesources/docs/acquisitions")
        {
            Content = JsonContent.Create(new StartKnowledgeAcquisitionRequest
            {
                Parameters = JsonSerializer.SerializeToElement(new { locale = "en-US" }),
                CorrelationId = "knowledge-correlation"
            })
        };
        repeatedRequest.Headers.Add("Idempotency-Key", "docs-acquisition-1");
        using var repeatedResponse = await client.SendAsync(repeatedRequest);
        var repeated = await repeatedResponse.Content.ReadFromJsonAsync<KnowledgeAcquisitionResource>();
        Assert.AreEqual(HttpStatusCode.Accepted, repeatedResponse.StatusCode);
        Assert.AreEqual(started.Name, repeated!.Name);

        using var conflictingRequest = new HttpRequestMessage(HttpMethod.Post, "/api/knowledgesources/docs/acquisitions")
        {
            Content = JsonContent.Create(new StartKnowledgeAcquisitionRequest
            {
                Parameters = JsonSerializer.SerializeToElement(new { locale = "fr-FR" })
            })
        };
        conflictingRequest.Headers.Add("Idempotency-Key", "docs-acquisition-1");
        using var conflict = await client.SendAsync(conflictingRequest);
        Assert.AreEqual(HttpStatusCode.Conflict, conflict.StatusCode);

        var history = await client.GetFromJsonAsync<KnowledgeAcquisitionResource[]>("/api/knowledgesources/docs/acquisitions");
        Assert.HasCount(1, history!);
    }

    [TestMethod]
    public async Task AcquisitionRejectsAFlowWithoutTheIngestionContract()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        await CreatePublishedFlowAsync(factory.Services, context, "plain-flow");
        await CreatePublishedFlowAsync(factory.Services, context, "retrieve-flow");
        _ = await factory.Services.GetRequiredService<KnowledgeSourceManagementService>().CreateAsync(new KnowledgeSourceResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            Metadata = new() { Name = "invalid-contract" },
            ScopeRef = ResourceScopeRef.Workspace(context.WorkspaceId),
            Definition = Properties("Invalid contract", "plain-flow", "retrieve-flow")
        }, default);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync("/api/knowledgesources/invalid-contract/acquisitions",
            new StartKnowledgeAcquisitionRequest());

        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        StringAssert.EndsWith(problem!.Type, "knowledge_ingestion_contract_required");
    }

    [TestMethod]
    public async Task AcquisitionEnforcesSingleActiveRunAndSupportsCancellationAndRetry()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        await CreatePublishedIngestionFlowAsync(factory.Services, context, "controlled-ingest", Guid.NewGuid().ToString("N"));
        await CreatePublishedFlowAsync(factory.Services, context, "controlled-retrieve");
        _ = await factory.Services.GetRequiredService<KnowledgeSourceManagementService>().CreateAsync(new KnowledgeSourceResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            Metadata = new() { Name = "controlled" },
            ScopeRef = ResourceScopeRef.Workspace(context.WorkspaceId),
            Definition = Properties("Controlled", "controlled-ingest", "controlled-retrieve")
        }, default);
        using var client = factory.CreateClient();
        using var startedResponse = await client.PostAsJsonAsync("/api/knowledgesources/controlled/acquisitions",
            new StartKnowledgeAcquisitionRequest { Parameters = JsonSerializer.SerializeToElement(new { page = 1 }) });
        var started = await startedResponse.Content.ReadFromJsonAsync<KnowledgeAcquisitionResource>();
        Assert.AreEqual(HttpStatusCode.Accepted, startedResponse.StatusCode);

        using var concurrent = await client.PostAsJsonAsync("/api/knowledgesources/controlled/acquisitions",
            new StartKnowledgeAcquisitionRequest { Parameters = JsonSerializer.SerializeToElement(new { page = 2 }) });
        Assert.AreEqual(HttpStatusCode.Conflict, concurrent.StatusCode);

        using var cancelledResponse = await client.PostAsync($"/api/knowledgeacquisitions/{started!.Name}/cancel", null);
        var cancelled = await cancelledResponse.Content.ReadFromJsonAsync<KnowledgeAcquisitionResource>();
        Assert.AreEqual(HttpStatusCode.OK, cancelledResponse.StatusCode);
        Assert.AreEqual(KnowledgeAcquisitionState.Cancelled, cancelled!.State);

        using var retriedResponse = await client.PostAsJsonAsync($"/api/knowledgeacquisitions/{started.Name}/retry",
            new RetryKnowledgeAcquisitionRequest { CorrelationId = "retry-correlation" });
        var retried = await retriedResponse.Content.ReadFromJsonAsync<KnowledgeAcquisitionResource>();
        Assert.AreEqual(HttpStatusCode.Accepted, retriedResponse.StatusCode);
        Assert.AreNotEqual(started.Name, retried!.Name);
        Assert.AreEqual(started.Name, retried.RetriedFrom);
        Assert.AreEqual(2, retried.Attempt);
        Assert.AreEqual("retry-correlation", retried.CorrelationId);
        Assert.AreEqual(1, retried.Parameters.GetProperty("page").GetInt32());
    }

    [TestMethod]
    public async Task AcquisitionArtifactReferencesCannotCrossWorkspaceBoundaries()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var staged = await factory.Services.GetRequiredService<ArtifactManagementService>().CreateStagedAsync(
            new("private.md", "text/markdown", new ArtifactProducer
            {
                Kind = ArtifactProducerKind.FlowRun,
                Id = "knowledge-isolation",
                FlowRunId = "knowledge-isolation"
            }), default);
        var otherWorkspaceId = Guid.NewGuid();
        var otherWorkspace = new Workspace(otherWorkspaceId, context.TenantId, $"knowledge-{otherWorkspaceId:N}",
            "Knowledge isolation", WorkspaceStatus.Initializing, DateTimeOffset.UtcNow);
        await factory.Services.GetRequiredService<IIdentityStore>().AddWorkspaceAsync(otherWorkspace, default);
        await factory.Services.GetRequiredService<IWorkspaceProvisioner>().ProvisionAsync(otherWorkspace, default);
        var validator = factory.Services.GetRequiredService<IKnowledgeArtifactReferenceValidator>();
        using var otherRequestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>()
            .Push(context with { WorkspaceId = otherWorkspaceId });

        var denied = await Assert.ThrowsAsync<KnowledgeAcquisitionException>(() => validator.ValidateAsync(
            otherWorkspaceId,
            [new KnowledgeAcquisitionArtifact
            {
                ArtifactId = staged.Value.ArtifactId.ToString(),
                Kind = KnowledgeArtifactKind.Staged,
                Disposition = KnowledgeArtifactDisposition.Publishable
            }],
            default));

        Assert.AreEqual("knowledge_ingestion_artifact_not_found", denied.Code);
    }

    [TestMethod]
    public async Task SnapshotsPublishDurableAcquisitionOutputsImmutablyAndSelectActiveHistory()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        await CreatePublishedIngestionFlowAsync(factory.Services, context, "snapshot-ingest", null);
        await CreatePublishedFlowAsync(factory.Services, context, "snapshot-retrieve");
        var source = await factory.Services.GetRequiredService<KnowledgeSourceManagementService>().CreateAsync(
            new KnowledgeSourceResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = KnowledgeResourceKinds.KnowledgeSource,
                Metadata = new() { Name = "snapshot-source" },
                ScopeRef = ResourceScopeRef.Workspace(context.WorkspaceId),
                Definition = Properties("Snapshot source", "snapshot-ingest", "snapshot-retrieve")
            }, default);
        using var client = factory.CreateClient();
        using var startedResponse = await client.PostAsJsonAsync("/api/knowledgesources/snapshot-source/acquisitions",
            new StartKnowledgeAcquisitionRequest());
        var started = await startedResponse.Content.ReadFromJsonAsync<KnowledgeAcquisitionResource>();
        Assert.AreEqual(HttpStatusCode.Accepted, startedResponse.StatusCode);
        await factory.Services.GetRequiredService<FlowRunService>().ExecuteAsync(
            new FlowRunQueueItem(started!.FlowRunId,
                new FlowRunScope(context.TenantId, new WorkspaceId(context.WorkspaceId), context.PrincipalId)), default);
        var completed = await client.GetFromJsonAsync<KnowledgeAcquisitionResource>(startedResponse.Headers.Location);
        Assert.AreEqual(KnowledgeAcquisitionState.Succeeded, completed!.State);

        var firstArtifact = await CreateDurableArtifactAsync(factory.Services, started.FlowRunId, "first");
        var secondArtifact = await CreateDurableArtifactAsync(factory.Services, started.FlowRunId, "second");
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var acquisitionAddress = ScopedResourceAddress.Create(ResourceScopeRef.Workspace(context.WorkspaceId),
            ResourceNamespace.Default, KnowledgeResourceKinds.KnowledgeAcquisition, started.Name);
        var storedAcquisition = await store.GetExactAsync<KnowledgeAcquisitionResource>(acquisitionAddress, default);
        var artifacts = new[] { firstArtifact, secondArtifact }.Select(value => new KnowledgeAcquisitionArtifact
        {
            ArtifactId = value.ArtifactId.ToString(),
            Kind = KnowledgeArtifactKind.Durable,
            Disposition = KnowledgeArtifactDisposition.Publishable,
            MediaType = value.Receipt.MediaType,
            Digest = value.Receipt.Sha256
        }).ToArray();
        _ = await store.PutExactAsync(ResourceScopeRef.Workspace(context.WorkspaceId), storedAcquisition!.Value with
        {
            Generation = checked(storedAcquisition.Value.Generation + 1),
            Manifest = new() { Artifacts = artifacts }
        }, storedAcquisition.ETag, false, default);

        var first = await PublishSnapshotAsync(client, started.Name, "snapshot-publication-1",
            new() { ArtifactIds = [firstArtifact.ArtifactId.ToString()] });
        var firstPublications = await client.GetFromJsonAsync<KnowledgeSnapshotPublicationResource[]>(
            $"/api/knowledgeacquisitions/{started.Name}/snapshot-publications");
        var firstPublication = firstPublications!.Single();
        var publicationAddress = ScopedResourceAddress.Create(ResourceScopeRef.Workspace(context.WorkspaceId),
            ResourceNamespace.Default, KnowledgeResourceKinds.KnowledgeSnapshotPublication, firstPublication.Name);
        var storedPublication = await store.GetExactAsync<KnowledgeSnapshotPublicationResource>(publicationAddress, default);
        _ = await store.PutExactAsync(ResourceScopeRef.Workspace(context.WorkspaceId), storedPublication!.Value with
        {
            Generation = checked(storedPublication.Value.Generation + 1),
            PublicationState = KnowledgeSnapshotPublicationState.Pending,
            SnapshotName = null,
            CompletedAt = null
        }, storedPublication.ETag, false, default);
        await factory.Services.GetRequiredService<KnowledgeSnapshotService>().RecoverPendingAsync(default);
        var recoveredPublication = await store.GetExactAsync<KnowledgeSnapshotPublicationResource>(publicationAddress, default);
        Assert.AreEqual(KnowledgeSnapshotPublicationState.Succeeded, recoveredPublication!.Value.PublicationState);
        Assert.AreEqual(first.Name, recoveredPublication.Value.SnapshotName);
        var second = await PublishSnapshotAsync(client, started.Name, "snapshot-publication-2",
            new() { ArtifactIds = [secondArtifact.ArtifactId.ToString()] });
        Assert.AreNotEqual(first.Name, second.Name);
        Assert.AreEqual(started.FlowRunId, first.IngestionFlowRunId);
        Assert.AreEqual(source.Value.Generation, first.KnowledgeSourceGeneration);
        Assert.HasCount(1, first.Artifacts);
        Assert.AreEqual(firstArtifact.Receipt.Sha256, first.Artifacts[0].Sha256);

        var active = await client.GetFromJsonAsync<KnowledgeSnapshotView>(
            "/api/knowledgesources/snapshot-source/snapshots/active");
        Assert.AreEqual(second.Name, active!.Snapshot.Name);
        Assert.AreEqual(KnowledgeSnapshotLifecycleState.Active, active.LifecycleState);
        var history = await client.GetFromJsonAsync<KnowledgeSnapshotView[]>(
            "/api/knowledgesources/snapshot-source/snapshots");
        Assert.HasCount(2, history!);
        Assert.AreEqual(KnowledgeSnapshotLifecycleState.Superseded,
            history!.Single(value => value.Snapshot.Name == first.Name).LifecycleState);
        var historical = await client.GetFromJsonAsync<KnowledgeSnapshotView>(
            $"/api/knowledgesnapshots/{first.Name}");
        Assert.AreEqual(first.Name, historical!.Snapshot.Name);
        Assert.AreEqual(KnowledgeSnapshotLifecycleState.Superseded, historical.LifecycleState);
        using var selectFirst = await client.PutAsJsonAsync(
            "/api/knowledgesources/snapshot-source/snapshots/active",
            new SelectActiveKnowledgeSnapshotRequest(first.Name));
        Assert.AreEqual(HttpStatusCode.OK, selectFirst.StatusCode);
        using var selectSecond = await client.PutAsJsonAsync(
            "/api/knowledgesources/snapshot-source/snapshots/active",
            new SelectActiveKnowledgeSnapshotRequest(second.Name));
        Assert.AreEqual(HttpStatusCode.OK, selectSecond.StatusCode);

        using var repeatRequest = SnapshotRequest(started.Name, "snapshot-publication-1",
            new() { ArtifactIds = [firstArtifact.ArtifactId.ToString()] });
        using var repeatedResponse = await client.SendAsync(repeatRequest);
        var repeated = await repeatedResponse.Content.ReadFromJsonAsync<KnowledgeSnapshotResource>();
        Assert.AreEqual(HttpStatusCode.Created, repeatedResponse.StatusCode);
        Assert.AreEqual(first.Name, repeated!.Name);

        using var conflictRequest = SnapshotRequest(started.Name, "snapshot-publication-1",
            new() { ArtifactIds = [firstArtifact.ArtifactId.ToString()], Activate = false });
        using var conflict = await client.SendAsync(conflictRequest);
        Assert.AreEqual(HttpStatusCode.Conflict, conflict.StatusCode);

        using var failedRequest = SnapshotRequest(started.Name, "snapshot-publication-failed",
            new() { ArtifactIds = [Guid.NewGuid().ToString("N")] });
        using var failedResponse = await client.SendAsync(failedRequest);
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, failedResponse.StatusCode);
        var activeAfterFailure = await client.GetFromJsonAsync<KnowledgeSnapshotView>(
            "/api/knowledgesources/snapshot-source/snapshots/active");
        Assert.AreEqual(second.Name, activeAfterFailure!.Snapshot.Name);
        var publications = await client.GetFromJsonAsync<KnowledgeSnapshotPublicationResource[]>(
            $"/api/knowledgeacquisitions/{started.Name}/snapshot-publications");
        Assert.AreEqual(1, publications!.Count(value =>
            value.PublicationState == KnowledgeSnapshotPublicationState.Failed));

        var immutable = await store.GetExactAsync<KnowledgeSnapshotResource>(ScopedResourceAddress.Create(
            ResourceScopeRef.Workspace(context.WorkspaceId), ResourceNamespace.Default,
            KnowledgeResourceKinds.KnowledgeSnapshot, first.Name), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.PutExactAsync(
            ResourceScopeRef.Workspace(context.WorkspaceId), immutable!.Value with { PublishedBy = Guid.NewGuid() },
            immutable.ETag, false, default));

        var sourceInUse = await Assert.ThrowsAsync<KnowledgeSourceValidationException>(() =>
            factory.Services.GetRequiredService<KnowledgeSourceManagementService>()
                .DeleteAsync(new("snapshot-source"), source.ETag, default));
        Assert.AreEqual("knowledge_source_in_use_by_snapshot", sourceInUse.Code);
        await CreatePublishedIngestionFlowAsync(factory.Services, context, "replacement-ingest", null);
        var sourceService = factory.Services.GetRequiredService<KnowledgeSourceManagementService>();
        var currentSource = await sourceService.GetAsync(new("snapshot-source"), default);
        _ = await sourceService.PutAsync(new("snapshot-source"), currentSource!.Value.Definition with
        {
            IngestionFlow = new() { Name = "replacement-ingest" }
        }, currentSource.ETag, default);
        var oldFlow = await factory.Services.GetRequiredService<FlowService>().GetAsync(
            new(context.WorkspaceId), new("snapshot-ingest"), default);
        var flowInUse = await Assert.ThrowsAsync<FlowValidationException>(() =>
            factory.Services.GetRequiredService<FlowService>().DeleteAsync(
                new(context.WorkspaceId), new("snapshot-ingest"), oldFlow!.ETag, default));
        Assert.AreEqual("flow_in_use_by_knowledge_snapshot", flowInUse.Code);
        var run = await factory.Services.GetRequiredService<FlowRunService>().GetAsync(
            new WorkspaceId(context.WorkspaceId), started.FlowRunId, default);
        var runInUse = await Assert.ThrowsAsync<FlowValidationException>(() =>
            factory.Services.GetRequiredService<FlowRunService>().DeleteAsync(started.FlowRunId, run!.ETag,
                new(context.TenantId, new(context.WorkspaceId), context.PrincipalId), default));
        Assert.AreEqual("flow_run_in_use_by_knowledge_snapshot", runInUse.Code);
    }

    [TestMethod]
    public async Task SnapshotArtifactResolutionRejectsAnotherWorkspace()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        FlowRunArtifactResource artifact;
        using (scopes.Push(context))
        {
            artifact = await CreateDurableArtifactAsync(factory.Services, "foreign-producer", "private");
            var otherWorkspaceId = Guid.NewGuid();
            var otherWorkspace = new Workspace(otherWorkspaceId, context.TenantId, $"snapshot-{otherWorkspaceId:N}",
                "Snapshot isolation", WorkspaceStatus.Initializing, DateTimeOffset.UtcNow);
            await factory.Services.GetRequiredService<IIdentityStore>().AddWorkspaceAsync(otherWorkspace, default);
            await factory.Services.GetRequiredService<IWorkspaceProvisioner>().ProvisionAsync(otherWorkspace, default);
            using var otherScope = scopes.Push(context with { WorkspaceId = otherWorkspaceId });
            var denied = await Assert.ThrowsAsync<KnowledgeSnapshotException>(() =>
                factory.Services.GetRequiredService<IKnowledgeSnapshotArtifactResolver>()
                    .ResolveAsync(otherWorkspaceId, [artifact.ArtifactId.ToString()], default));
        Assert.AreEqual("knowledge_snapshot_artifact_not_found", denied.Code);
        }
    }

    [TestMethod]
    public async Task RetrievalRoutesTrustedOperationsThroughExactFlowsAndSnapshotBoundaries()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var firstArtifact = await CreateDurableArtifactAsync(factory.Services, "retrieval-producer", "first");
        var output = JsonSerializer.SerializeToElement(new
        {
            items = new[]
            {
                new
                {
                    id = "first-result",
                    artifactId = firstArtifact.ArtifactId.ToString(),
                    content = "first",
                    mediaType = "text/markdown",
                    score = 1.0
                }
            },
            citations = new[]
            {
                new { artifactId = firstArtifact.ArtifactId.ToString(), locator = "line:1", excerpt = "first" }
            },
            answer = "first",
            continuationToken = "next-page"
        });
        await CreatePublishedFlowAsync(factory.Services, context, "retrieval-ingest");
        await CreatePublishedRetrievalFlowAsync(factory.Services, context, "retrieval-flow", output);
        var source = await factory.Services.GetRequiredService<KnowledgeSourceManagementService>().CreateAsync(
            new KnowledgeSourceResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = KnowledgeResourceKinds.KnowledgeSource,
                Metadata = new() { Name = "retrieval-source" },
                ScopeRef = ResourceScopeRef.Workspace(context.WorkspaceId),
                Definition = Properties("Retrieval source", "retrieval-ingest", "retrieval-flow")
            }, default);
        var firstSnapshot = await CreateActiveSnapshotAsync(factory.Services, context, source.Value,
            firstArtifact, "snapshot-retrieval-first");
        using var client = factory.CreateClient();

        using var searchResponse = await client.PostAsJsonAsync("/api/knowledgesources/retrieval-source/search",
            new SearchKnowledgeRequest
            {
                Query = "documentation",
                Filters = new Dictionary<string, string> { ["locale"] = "en-US" },
                Limit = 5,
                CorrelationId = "retrieval-correlation"
            });
        Assert.AreEqual(HttpStatusCode.OK, searchResponse.StatusCode,
            await searchResponse.Content.ReadAsStringAsync());
        var search = await searchResponse.Content.ReadFromJsonAsync<KnowledgeRetrievalResult>();
        Assert.IsNotNull(search);
        Assert.AreEqual(KnowledgeSourceOperation.Search, search.Operation);
        Assert.AreEqual(firstSnapshot.Name, search.SnapshotName);
        Assert.AreEqual("1.0.0", search.RetrievalFlow.Version);
        Assert.AreEqual(KnowledgeFlowContracts.Retrieval, search.RetrievalFlow.Contract);
        Assert.AreEqual("retrieval-correlation", search.CorrelationId);
        Assert.HasCount(1, search.Items);
        Assert.AreEqual(firstArtifact.ArtifactId.ToString(), search.Items[0].ArtifactId);

        using var queryResponse = await client.PostAsJsonAsync("/api/knowledgesources/retrieval-source/query",
            new QueryKnowledgeRequest
            {
                Question = "What is first?",
                SnapshotName = firstSnapshot.Name,
                MaximumItems = 2,
                MaximumOutputCharacters = 100
            });
        var query = await queryResponse.Content.ReadFromJsonAsync<KnowledgeRetrievalResult>();
        Assert.AreEqual(HttpStatusCode.OK, queryResponse.StatusCode,
            await queryResponse.Content.ReadAsStringAsync());
        Assert.AreEqual("first", query!.Answer);
        Assert.HasCount(1, query.Citations);

        using var readResponse = await client.PostAsJsonAsync("/api/knowledgesources/retrieval-source/read",
            new ReadKnowledgeRequest
            {
                ArtifactId = firstArtifact.ArtifactId.ToString(),
                SnapshotName = firstSnapshot.Name,
                Length = 5
            });
        Assert.AreEqual(HttpStatusCode.OK, readResponse.StatusCode,
            await readResponse.Content.ReadAsStringAsync());

        using var exposureResponse = await client.PostAsJsonAsync(
            "/api/knowledgesources/retrieval-source/tool-exposure",
            new PublishKnowledgeSourceToolExposureRequest { Version = "1.0.0" });
        Assert.AreEqual(HttpStatusCode.Created, exposureResponse.StatusCode,
            await exposureResponse.Content.ReadAsStringAsync());
        var tool = await factory.Services.GetRequiredService<IToolDefinitionExecutor>().ExecuteAsync(
            new ToolDefinitionInvocation(
                context.TenantId,
                new WorkspaceId(context.WorkspaceId),
                context.PrincipalId,
                default,
                "retrieval-source.search",
                "retrieval-tool-call",
                "retrieval-tool-correlation",
                JsonSerializer.SerializeToElement(new
                {
                    request = new
                    {
                        query = "documentation",
                        limit = 5,
                        snapshotName = firstSnapshot.Name
                    }
                }),
                ToolDefinitionCallerKind.Agent,
                "retrieval-agent"), default);
        var toolItems = tool.Output!.Value.GetProperty("items");
        Assert.AreEqual(1, toolItems.GetArrayLength());
        Assert.AreEqual(firstArtifact.ArtifactId.ToString(),
            toolItems[0].GetProperty("artifactId").GetString());

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(client.BaseAddress!, "mcp"),
                Name = "knowledge-retrieval-test"
            },
            client,
            NullLoggerFactory.Instance,
            ownsHttpClient: false);
        await using var mcp = await McpClient.CreateAsync(transport, loggerFactory: NullLoggerFactory.Instance);
        var publishedTool = (await mcp.ListToolsAsync()).Single(value => value.Name == "retrieval-source.search");
        Assert.AreEqual("retrieval-source",
            publishedTool.ProtocolTool.Meta?["agentstration/knowledgeSource"]?.GetValue<string>());
        Assert.AreEqual("search",
            publishedTool.ProtocolTool.Meta?["agentstration/knowledgeOperation"]?.GetValue<string>());
        Assert.IsFalse(publishedTool.JsonSchema.GetProperty("properties")
            .TryGetProperty("knowledgeSourceId", out _));
        var mcpResult = await mcp.CallToolAsync("retrieval-source.search", new Dictionary<string, object?>
        {
            ["request"] = new Dictionary<string, object?>
            {
                ["query"] = "documentation",
                ["limit"] = 5,
                ["snapshotName"] = firstSnapshot.Name
            }
        });
        Assert.AreNotEqual(true, mcpResult.IsError);
        StringAssert.Contains(JsonSerializer.Serialize(mcpResult.StructuredContent), firstArtifact.ArtifactId.ToString());
        var mcpReceipt = JsonSerializer.Serialize(mcpResult.Meta);
        StringAssert.Contains(mcpReceipt, firstSnapshot.Name);
        StringAssert.Contains(mcpReceipt, firstArtifact.ArtifactId.ToString());

        var runtimeOutput = await factory.Services.GetRequiredService<IToolInvoker>().InvokeAsync(
            new ToolExecutionContext
            {
                OwnerKind = ToolExecutionOwnerKind.RuntimeRun,
                ToolCallId = "runtime-knowledge-call",
                InvocationId = "runtime-knowledge-invocation",
                ToolId = AgentstrationToolProvider.ToolResourceName("retrieval-source.search"),
                ToolNamespace = ResourceNamespace.Default,
                ToolName = "retrieval-source.search",
                ToolProviderId = AgentstrationToolProvider.Name,
                ToolProviderNamespace = ResourceNamespace.Default,
                ExternalToolId = "retrieval-source.search",
                TenantId = context.TenantId,
                WorkspaceId = new WorkspaceId(context.WorkspaceId),
                PrincipalId = context.PrincipalId,
                RunId = "runtime-run-knowledge",
                FlowStepId = "agent-step",
                AgentId = "retrieval-agent",
                AgentRevisionId = "retrieval-agent-r1",
                CorrelationId = "runtime-knowledge-correlation",
                Arguments = JsonSerializer.SerializeToElement(new
                {
                    request = new
                    {
                        query = "documentation",
                        limit = 5,
                        snapshotName = firstSnapshot.Name
                    }
                })
            });
        Assert.IsNotNull(runtimeOutput);
        var scope = new FlowRunScope(context.TenantId, new WorkspaceId(context.WorkspaceId), context.PrincipalId);
        var retrievalRuns = await factory.Services.GetRequiredService<FlowRunService>().ListAsync(
            new FlowId("retrieval-flow"), null, 0, 100, scope, default);
        var runtimeRun = retrievalRuns.Items.Select(value => value.Value).Single(value =>
            value.Input.GetProperty("caller").GetProperty("runtimeRunId").GetString() == "runtime-run-knowledge");
        Assert.AreEqual("retrieval-agent", runtimeRun.Input.GetProperty("caller").GetProperty("agentId").GetString());
        Assert.AreEqual("retrieval-agent-r1", runtimeRun.Input.GetProperty("caller").GetProperty("agentRevisionId").GetString());
        Assert.AreEqual("agent-step", runtimeRun.Input.GetProperty("caller").GetProperty("flowStepId").GetString());
        Assert.AreEqual("runtime-knowledge-call", runtimeRun.Input.GetProperty("caller").GetProperty("toolCallId").GetString());
        Assert.AreEqual("runtime-knowledge-invocation", runtimeRun.CausationId);

        var secondArtifact = await CreateDurableArtifactAsync(factory.Services, "retrieval-producer-2", "second");
        _ = await CreateActiveSnapshotAsync(factory.Services, context, source.Value,
            secondArtifact, "snapshot-retrieval-second");
        var mcpFailure = await mcp.CallToolAsync("retrieval-source.search", new Dictionary<string, object?>
        {
            ["request"] = new Dictionary<string, object?> { ["query"] = "documentation", ["limit"] = 5 }
        });
        Assert.AreEqual(true, mcpFailure.IsError);
        StringAssert.Contains(JsonSerializer.Serialize(mcpFailure.Meta), "knowledge_retrieval_output_outside_snapshot");
        Assert.IsTrue(mcpFailure.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>()
            .All(value => value.Text.Length <= 2_000));
        using var escaped = await client.PostAsJsonAsync("/api/knowledgesources/retrieval-source/search",
            new SearchKnowledgeRequest { Query = "documentation", Limit = 5 });
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, escaped.StatusCode);
        var escapedProblem = await escaped.Content.ReadFromJsonAsync<ProblemDetails>();
        StringAssert.EndsWith(escapedProblem!.Type, "knowledge_retrieval_output_outside_snapshot");

        using var historical = await client.PostAsJsonAsync("/api/knowledgesources/retrieval-source/search",
            new SearchKnowledgeRequest { Query = "documentation", Limit = 5, SnapshotName = firstSnapshot.Name });
        Assert.AreEqual(HttpStatusCode.OK, historical.StatusCode,
            await historical.Content.ReadAsStringAsync());
        using var wrongRead = await client.PostAsJsonAsync("/api/knowledgesources/retrieval-source/read",
            new ReadKnowledgeRequest
            {
                ArtifactId = secondArtifact.ArtifactId.ToString(),
                SnapshotName = firstSnapshot.Name,
                Length = 1
            });
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, wrongRead.StatusCode);
        var wrongReadProblem = await wrongRead.Content.ReadFromJsonAsync<ProblemDetails>();
        StringAssert.EndsWith(wrongReadProblem!.Type, "knowledge_read_artifact_outside_snapshot");

        var otherWorkspaceId = Guid.NewGuid();
        var otherWorkspace = new Workspace(otherWorkspaceId, context.TenantId, $"retrieval-{otherWorkspaceId:N}",
            "Retrieval isolation", WorkspaceStatus.Initializing, DateTimeOffset.UtcNow);
        await factory.Services.GetRequiredService<IIdentityStore>().AddWorkspaceAsync(otherWorkspace, default);
        await factory.Services.GetRequiredService<IWorkspaceProvisioner>().ProvisionAsync(otherWorkspace, default);
        using var otherScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>()
            .Push(context with { WorkspaceId = otherWorkspaceId });
        _ = await Assert.ThrowsAsync<AuthorizationDeniedException>(() => factory.Services
            .GetRequiredService<KnowledgeRetrievalService>().SearchAsync(new("retrieval-source"),
                new SearchKnowledgeRequest { Query = "documentation" }, default));

        var audit = await factory.Services.GetRequiredService<ISecurityAuditStore>().ListLatestAsync(100, default);
        Assert.IsTrue(audit.Any(value => value.WorkspaceId == context.WorkspaceId
            && value.Action == SecurityAuditActions.KnowledgeRetrievalCompleted));
        Assert.IsTrue(audit.Any(value => value.WorkspaceId == context.WorkspaceId
            && value.Action == SecurityAuditActions.KnowledgeRetrievalFailed));
    }

    [TestMethod]
    public async Task RetrievalCapabilitiesControlToolExposureAndRejectUnknownDeclarations()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        await CreatePublishedFlowAsync(factory.Services, context, "capability-ingest");
        await CreatePublishedRetrievalFlowAsync(factory.Services, context, "search-only", capabilities:
            KnowledgeFlowContracts.Search);
        _ = await factory.Services.GetRequiredService<KnowledgeSourceManagementService>().CreateAsync(
            new KnowledgeSourceResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = KnowledgeResourceKinds.KnowledgeSource,
                Metadata = new() { Name = "search-only-source" },
                ScopeRef = ResourceScopeRef.Workspace(context.WorkspaceId),
                Definition = Properties("Search only", "capability-ingest", "search-only")
            }, default);
        var exposure = await factory.Services.GetRequiredService<KnowledgeSourceToolExposureService>()
            .PublishAsync(new("search-only-source"), "1.0.0", false, default);
        Assert.HasCount(1, exposure.Value.Operations);
        Assert.AreEqual(KnowledgeSourceOperation.Search, exposure.Value.Operations[0].Operation);

        var rejected = await Assert.ThrowsAsync<FlowValidationException>(() =>
            CreatePublishedRetrievalFlowAsync(factory.Services, context, "invalid-capability", capabilities:
                "knowledge.unsupported/v1"));
        Assert.AreEqual("knowledge_retrieval_capabilities_invalid", rejected.Code);
    }

    [TestMethod]
    public async Task KnowledgeSourceExposureCreatesSourceSpecificToolsWithFixedRoutingArguments()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        await CreatePublishedFlowAsync(factory.Services, context, "docs-ingest");
        await CreatePublishedRetrievalFlowAsync(factory.Services, context, "docs-retrieve");
        var sources = factory.Services.GetRequiredService<KnowledgeSourceManagementService>();
        _ = await sources.CreateAsync(new KnowledgeSourceResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            Metadata = new ResourceMetadata { Name = "agentstration-documentation" },
            ScopeRef = ResourceScopeRef.Workspace(context.WorkspaceId),
            Definition = Properties("Agentstration documentation", "docs-ingest", "docs-retrieve")
        }, default);
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(
            "/api/knowledgesources/agentstration-documentation/tool-exposure",
            new PublishKnowledgeSourceToolExposureRequest { Version = "1.0.0" });

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var exposure = await response.Content.ReadFromJsonAsync<KnowledgeSourceToolExposureResource>();
        Assert.IsNotNull(exposure);
        Assert.HasCount(3, exposure.Operations);
        CollectionAssert.AreEquivalent(new[] { "search", "query", "read" },
            exposure.Operations.Select(value => value.Route).ToArray());
        var definitions = factory.Services.GetRequiredService<ToolDefinitionService>();
        var search = await definitions.GetAsync("agentstration-documentation.search", default, default);
        Assert.IsNotNull(search);
        Assert.AreEqual("agentstration-documentation", search.Value.Definition.FixedArguments?.GetProperty("knowledgeSourceId").GetString());
        Assert.AreEqual("search", search.Value.Definition.FixedArguments?.GetProperty("operation").GetString());
        var publicProperties = search.Value.Definition.InputSchema.GetProperty("properties");
        Assert.IsFalse(publicProperties.TryGetProperty("knowledgeSourceId", out _));
        Assert.IsFalse(publicProperties.TryGetProperty("operation", out _));
        Assert.IsTrue(publicProperties.TryGetProperty("request", out _));
        var executor = factory.Services.GetRequiredService<IToolDefinitionExecutor>();
        var rejected = await Assert.ThrowsAsync<ToolDefinitionInvocationException>(() => executor.ExecuteAsync(
            new ToolDefinitionInvocation(
                context.TenantId,
                new WorkspaceId(context.WorkspaceId),
                context.PrincipalId,
                default,
                "agentstration-documentation.search",
                "fixed-argument-test",
                null,
                JsonSerializer.SerializeToElement(new
                {
                    knowledgeSourceId = "another-source",
                    request = new { query = "override" }
                }),
                ToolDefinitionCallerKind.Agent,
                "test-agent"), default));
        Assert.AreEqual("tool_definition_fixed_argument_override", rejected.Code);
        var published = await factory.Services.GetRequiredService<ToolSetService>()
            .GetVersionAsync(default, "agentstration-documentation", "1.0.0", default);
        Assert.IsNotNull(published);
        Assert.HasCount(3, published.Value.Members);
        Assert.IsTrue(published.Value.Metadata.Tags.ContainsKey("agentstration.io/category"));
    }

    [TestMethod]
    public async Task KnowledgeSourceCrudReportsReadinessAndProtectsItsFlows()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        await CreatePublishedFlowAsync(factory.Services, context, "documentation-ingest");
        await CreatePublishedFlowAsync(factory.Services, context, "documentation-retrieve");
        using var client = factory.CreateClient();

        using var createdResponse = await client.PostAsJsonAsync("/api/knowledgesources", new CreateKnowledgeSourceRequest(
            "agentstration-documentation",
            Properties("Agentstration documentation", "documentation-ingest", "documentation-retrieve")));

        Assert.AreEqual(HttpStatusCode.Created, createdResponse.StatusCode);
        Assert.IsNotNull(createdResponse.Headers.ETag);
        var created = await createdResponse.Content.ReadFromJsonAsync<KnowledgeSourceResource>();
        Assert.IsNotNull(created);
        Assert.AreEqual(ResourceScopeRef.Workspace(context.WorkspaceId), created.ScopeRef);
        Assert.AreEqual("True", created.Status.Conditions.Single(value => value.Type == "Ready").Status);

        var readiness = await client.GetFromJsonAsync<KnowledgeSourceReadiness>(
            "/api/knowledgesources/agentstration-documentation/readiness?namespace=default");
        Assert.IsNotNull(readiness);
        Assert.IsTrue(readiness.Ready);
        Assert.AreEqual("1.0.0", readiness.Ingestion?.Version);
        Assert.AreEqual("1.0.0", readiness.Retrieval?.Version);

        var flows = factory.Services.GetRequiredService<FlowService>();
        var workspaceId = new WorkspaceId(context.WorkspaceId);
        var ingestion = await flows.GetAsync(workspaceId, new("documentation-ingest"), default);
        Assert.IsNotNull(ingestion);
        var inUse = await Assert.ThrowsAsync<FlowValidationException>(() =>
            flows.DeleteAsync(workspaceId, new("documentation-ingest"), ingestion.ETag, default));
        Assert.AreEqual("flow_in_use_by_knowledge_source", inUse.Code);

        using var disable = new HttpRequestMessage(HttpMethod.Put,
            "/api/knowledgesources/agentstration-documentation/enabled?namespace=default")
        {
            Content = JsonContent.Create(new SetKnowledgeSourceEnabledRequest(false))
        };
        disable.Headers.IfMatch.Add(createdResponse.Headers.ETag!);
        using var disabledResponse = await client.SendAsync(disable);
        Assert.AreEqual(HttpStatusCode.OK, disabledResponse.StatusCode);
        var disabled = await disabledResponse.Content.ReadFromJsonAsync<KnowledgeSourceResource>();
        Assert.IsFalse(disabled!.Definition.Enabled);
        Assert.AreEqual("False", disabled.Status.Conditions.Single(value => value.Type == "Ready").Status);

        using var delete = new HttpRequestMessage(HttpMethod.Delete,
            "/api/knowledgesources/agentstration-documentation?namespace=default");
        delete.Headers.IfMatch.Add(disabledResponse.Headers.ETag!);
        using var deleted = await client.SendAsync(delete);
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.IsNull(await factory.Services.GetRequiredService<KnowledgeSourceManagementService>()
            .GetAsync(new("agentstration-documentation"), default));

        var audit = await factory.Services.GetRequiredService<ISecurityAuditStore>().ListLatestAsync(100, default);
        var actions = audit.Where(value => value.WorkspaceId == context.WorkspaceId)
            .Select(value => value.Action)
            .ToArray();
        CollectionAssert.IsSubsetOf(
            new[]
            {
                SecurityAuditActions.KnowledgeSourceCreated,
                SecurityAuditActions.KnowledgeSourceDisabled,
                SecurityAuditActions.KnowledgeSourceDeleted
            },
            actions);
        Assert.IsTrue(audit.Where(value => actions.Contains(value.Action, StringComparer.Ordinal))
            .All(value => !string.IsNullOrWhiteSpace(value.CorrelationId)));
    }

    [TestMethod]
    public async Task EnabledKnowledgeSourceRequiresTwoPublishedFlowBindings()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        await CreatePublishedFlowAsync(factory.Services, context, "available-flow");
        using var client = factory.CreateClient();

        using var incomplete = await client.PostAsJsonAsync("/api/knowledgesources", new CreateKnowledgeSourceRequest(
            "incomplete",
            new KnowledgeSourceProperties
            {
                DisplayName = "Incomplete",
                IngestionFlow = new() { Name = "available-flow" }
            }));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, incomplete.StatusCode);
        var incompleteProblem = await incomplete.Content.ReadFromJsonAsync<ProblemDetails>();
        StringAssert.EndsWith(incompleteProblem!.Type, "knowledge_source_flow_bindings_required");

        using var unresolved = await client.PostAsJsonAsync("/api/knowledgesources", new CreateKnowledgeSourceRequest(
            "unresolved",
            Properties("Unresolved", "available-flow", "missing-flow")));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, unresolved.StatusCode);
        var unresolvedProblem = await unresolved.Content.ReadFromJsonAsync<ProblemDetails>();
        StringAssert.EndsWith(unresolvedProblem!.Type, "knowledge_source_flow_unavailable");

        using var draftResponse = await client.PostAsJsonAsync("/api/knowledgesources", new CreateKnowledgeSourceRequest(
            "draft-source",
            new KnowledgeSourceProperties { DisplayName = "Draft source", Enabled = false }));
        Assert.AreEqual(HttpStatusCode.Created, draftResponse.StatusCode);
        var readiness = await client.GetFromJsonAsync<KnowledgeSourceReadiness>(
            "/api/knowledgesources/draft-source/readiness");
        Assert.IsFalse(readiness!.Ready);
        Assert.HasCount(3, readiness.Issues);
    }

    [TestMethod]
    public async Task KnowledgeSourceCannotBeCreatedInAnotherWorkspace()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var service = factory.Services.GetRequiredService<KnowledgeSourceManagementService>();
        var resource = new KnowledgeSourceResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            Metadata = new() { Name = "foreign-source" },
            ScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()),
            Definition = new() { DisplayName = "Foreign source", Enabled = false }
        };

        var denied = await Assert.ThrowsAsync<ResourceScopePolicyException>(() => service.CreateAsync(resource, default));
        StringAssert.Contains(denied.Message, "does not exist");
    }

    [TestMethod]
    public async Task KnowledgeSourceSupportsExactPublishedFlowVersionsAndNamespaces()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var ns = new ResourceNamespace("documentation");
        await CreatePublishedFlowAsync(factory.Services, context, "ingest", ns);
        await CreatePublishedFlowAsync(factory.Services, context, "retrieve", ns);
        var service = factory.Services.GetRequiredService<KnowledgeSourceManagementService>();
        var stored = await service.CreateAsync(new KnowledgeSourceResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSource,
            Metadata = new() { Name = "product-docs", Namespace = ns },
            ScopeRef = ResourceScopeRef.Workspace(context.WorkspaceId),
            Definition = new()
            {
                DisplayName = "Product docs",
                IngestionFlow = new() { Name = "ingest", Version = "1.0.0", UseActiveVersion = false },
                RetrievalFlow = new() { Name = "retrieve", Version = "1.0.0", UseActiveVersion = false }
            }
        }, default);

        var readiness = await service.GetReadinessAsync(new("product-docs", ns), default);
        Assert.IsTrue(readiness.Ready);
        Assert.IsFalse(readiness.Ingestion!.UsesActiveVersion);
        Assert.AreEqual(ns, readiness.Retrieval!.Namespace);
        Assert.AreEqual(1, stored.Value.Generation);
    }

    private static KnowledgeSourceProperties Properties(
        string displayName,
        string ingestion,
        string retrieval) => new()
        {
            DisplayName = displayName,
            IngestionFlow = new() { Name = ingestion },
            RetrievalFlow = new() { Name = retrieval }
        };

    private static async Task CreatePublishedFlowAsync(
        IServiceProvider services,
        RequestContext context,
        string name,
        ResourceNamespace? resourceNamespace = null)
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { knowledgeSourceId = new { type = "string" } }
        });
        var flows = services.GetRequiredService<FlowService>();
        var workspaceId = new WorkspaceId(context.WorkspaceId);
        var ns = resourceNamespace ?? ResourceNamespace.Default;
        await flows.CreateAsync(workspaceId, new CreateFlowCommand(
            name,
            null,
            "1.0.0",
            true,
            new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "unused")),
            Graph: new FlowGraphDefinition
            {
                EntryStep = "input",
                InputSchema = schema.Clone(),
                OutputSchema = schema.Clone(),
                Steps =
                [
                    new InputFlowStepDefinition { Name = "input", Schema = schema.Clone() },
                    new OutputFlowStepDefinition { Name = "output", OutputMapping = JsonSerializer.SerializeToElement("${input}") }
                ],
                Transitions = [new("input-output", "input", "completed", "output")]
            }), ns, default);
        await flows.PublishVersionAsync(workspaceId, new(name, ns), "1.0.0", true, default);
    }

    private static async Task CreatePublishedRetrievalFlowAsync(
        IServiceProvider services,
        RequestContext context,
        string name,
        JsonElement? output = null,
        string? capabilities = null)
    {
        var inputSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                knowledgeSourceId = new { type = "string" },
                knowledgeSourceUid = new { type = "string" },
                knowledgeSourceGeneration = new { type = "integer" },
                operation = new { type = "string" },
                snapshot = new { type = "object" },
                request = new { type = "object" },
                caller = new { type = "object" },
                correlationId = new { type = "string" },
                retrievalId = new { type = "string" }
            },
            required = new[]
            {
                "knowledgeSourceId", "knowledgeSourceUid", "knowledgeSourceGeneration", "operation",
                "snapshot", "request", "caller", "correlationId", "retrievalId"
            },
            additionalProperties = false
        });
        var outputSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                items = new { type = "array" },
                citations = new { type = "array" },
                answer = new { type = "string" },
                continuationToken = new { type = "string" }
            },
            required = new[] { "items", "citations" },
            additionalProperties = false
        });
        var result = output ?? JsonSerializer.SerializeToElement(new
        {
            items = Array.Empty<object>(),
            citations = Array.Empty<object>()
        });
        var flows = services.GetRequiredService<FlowService>();
        var workspaceId = new WorkspaceId(context.WorkspaceId);
        await flows.CreateAsync(workspaceId, new CreateFlowCommand(
            name, null, "1.0.0", true,
            new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "unused")),
            new Dictionary<string, string>
            {
                [KnowledgeFlowContracts.MetadataKey] = KnowledgeFlowContracts.Retrieval,
                [KnowledgeFlowContracts.CapabilitiesMetadataKey] = capabilities ?? string.Join(',',
                    KnowledgeFlowContracts.Search, KnowledgeFlowContracts.Query, KnowledgeFlowContracts.Read)
            },
            Graph: new FlowGraphDefinition
            {
                EntryStep = "input",
                InputSchema = inputSchema,
                OutputSchema = outputSchema,
                Steps =
                [
                    new InputFlowStepDefinition { Name = "input", Schema = inputSchema },
                    new OutputFlowStepDefinition { Name = "output", OutputMapping = result }
                ],
                Transitions = [new("input-output", "input", "completed", "output")]
            }), default, default);
        await flows.PublishVersionAsync(workspaceId, new(name), "1.0.0", true, default);
    }

    private static async Task CreatePublishedIngestionFlowAsync(
        IServiceProvider services,
        RequestContext context,
        string name,
        string? artifactId)
    {
        var inputSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                knowledgeSourceId = new { type = "string" },
                parameters = new { type = "object" },
                caller = new { type = "object" },
                correlationId = new { type = "string" },
                acquisitionId = new { type = "string" }
            },
            required = new[] { "knowledgeSourceId", "parameters", "caller", "correlationId", "acquisitionId" }
        });
        var outputArtifacts = artifactId is null
            ? Array.Empty<object>()
            : [new { artifactId, kind = "staged", disposition = "publishable", name = "Documentation", mediaType = "text/markdown" }];
        var output = JsonSerializer.SerializeToElement(new { artifacts = outputArtifacts });
        var outputSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { artifacts = new { type = "array" } },
            required = new[] { "artifacts" }
        });
        var flows = services.GetRequiredService<FlowService>();
        var workspaceId = new WorkspaceId(context.WorkspaceId);
        await flows.CreateAsync(workspaceId, new CreateFlowCommand(
            name, null, "1.0.0", true,
            new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "unused")),
            new Dictionary<string, string> { [KnowledgeFlowContracts.MetadataKey] = KnowledgeFlowContracts.Ingestion },
            new FlowGraphDefinition
            {
                EntryStep = "input",
                InputSchema = inputSchema,
                OutputSchema = outputSchema,
                Steps =
                [
                    new InputFlowStepDefinition { Name = "input", Schema = inputSchema },
                    new OutputFlowStepDefinition { Name = "output", OutputMapping = output }
                ],
                Transitions = [new("input-output", "input", "completed", "output")]
            }), default, default);
        await flows.PublishVersionAsync(workspaceId, new(name), "1.0.0", true, default);
    }

    private static async Task<FlowRunArtifactResource> CreateDurableArtifactAsync(
        IServiceProvider services,
        string producerFlowRunId,
        string content)
    {
        var artifacts = services.GetRequiredService<ArtifactManagementService>();
        var staged = await artifacts.CreateStagedAsync(new($"{content}.md", "text/markdown", new ArtifactProducer
        {
            Kind = ArtifactProducerKind.FlowRun,
            Id = producerFlowRunId,
            FlowRunId = producerFlowRunId,
            FlowStepId = "output"
        }), default);
        _ = await artifacts.WriteAsync(staged.Value.ArtifactId, 0,
            System.Text.Encoding.UTF8.GetBytes(content), default);
        var sealedArtifact = await artifacts.SealAsync(staged.Value.ArtifactId, default);
        return (await artifacts.CompleteFlowRunArtifactAsync(staged.Value.ArtifactId,
            new(producerFlowRunId, "output", new ArtifactStorageReceipt
            {
                StorageFlowRunId = $"storage-{Guid.NewGuid():N}",
                OpaqueReference = $"knowledge/{Guid.NewGuid():N}",
                MediaType = sealedArtifact.Value.MediaType,
                Length = sealedArtifact.Value.Length,
                Sha256 = sealedArtifact.Value.Sha256!,
                Provenance = new Dictionary<string, string> { ["backend"] = "test" }
            }), default)).Value;
    }

    private static async Task<KnowledgeSnapshotResource> CreateActiveSnapshotAsync(
        IServiceProvider services,
        RequestContext context,
        KnowledgeSourceResource source,
        FlowRunArtifactResource artifact,
        string name)
    {
        var store = services.GetRequiredService<IResourceStore>();
        var scopeRef = ResourceScopeRef.Workspace(context.WorkspaceId);
        var snapshot = (await store.CreateImmutableAsync(new KnowledgeSnapshotResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSnapshot,
            Metadata = new() { Name = name, Namespace = source.Namespace },
            ScopeRef = scopeRef,
            Generation = 1,
            Status = new() { ProvisioningState = ProvisioningState.Succeeded },
            KnowledgeSourceUid = source.Uid,
            KnowledgeSourceName = source.Name,
            KnowledgeSourceNamespace = source.Namespace,
            KnowledgeSourceGeneration = source.Generation,
            AcquisitionId = $"acquisition-{name}",
            AcquisitionUid = Guid.NewGuid(),
            AcquiredAt = DateTimeOffset.UtcNow,
            IngestionFlow = new("retrieval-ingest", source.Namespace, "1.0.0", false, null, null,
                KnowledgeFlowContracts.Ingestion),
            IngestionFlowRunId = artifact.ProducerFlowRunId,
            PublicationId = $"publication-{name}",
            RequestHash = name,
            PublishedAt = DateTimeOffset.UtcNow,
            PublishedBy = context.PrincipalId,
            Artifacts =
            [
                new KnowledgeSnapshotArtifact
                {
                    ArtifactId = artifact.ArtifactId.ToString(),
                    ProducerFlowRunId = artifact.ProducerFlowRunId,
                    ProducerFlowStepId = artifact.ProducerFlowStepId,
                    StorageFlowRunId = artifact.Receipt.StorageFlowRunId,
                    MediaType = artifact.Receipt.MediaType,
                    Length = artifact.Receipt.Length,
                    Sha256 = artifact.Receipt.Sha256,
                    Provenance = artifact.Receipt.Provenance
                }
            ]
        }, default)).Value;
        var observedName = $"snapshot-state-{source.Uid:N}";
        var address = ScopedResourceAddress.Create(scopeRef, source.Namespace,
            KnowledgeResourceKinds.KnowledgeSnapshotObservedState, observedName);
        var current = await store.GetExactAsync<KnowledgeSnapshotObservedResource>(address, default);
        _ = await store.PutExactAsync(scopeRef, new KnowledgeSnapshotObservedResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = KnowledgeResourceKinds.KnowledgeSnapshotObservedState,
            Metadata = new() { Name = observedName, Namespace = source.Namespace },
            ScopeRef = scopeRef,
            Uid = current?.Value.Uid ?? Guid.Empty,
            Generation = current is null ? 1 : checked(current.Value.Generation + 1),
            Status = new() { ProvisioningState = ProvisioningState.Succeeded },
            KnowledgeSourceUid = source.Uid,
            ActiveSnapshotName = snapshot.Name,
            ActiveSnapshotUid = snapshot.Uid,
            LastPublishedAt = DateTimeOffset.UtcNow,
            LastAttemptAt = DateTimeOffset.UtcNow
        }, current?.ETag, current is null, default);
        return snapshot;
    }

    private static HttpRequestMessage SnapshotRequest(
        string acquisitionId,
        string idempotencyKey,
        PublishKnowledgeSnapshotRequest body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/knowledgeacquisitions/{Uri.EscapeDataString(acquisitionId)}/snapshots")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        return request;
    }

    private static async Task<KnowledgeSnapshotResource> PublishSnapshotAsync(
        HttpClient client,
        string acquisitionId,
        string idempotencyKey,
        PublishKnowledgeSnapshotRequest body)
    {
        using var request = SnapshotRequest(acquisitionId, idempotencyKey, body);
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<KnowledgeSnapshotResource>())!;
    }
}
