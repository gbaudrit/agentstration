using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Artifacts;
using Agentstration.Artifacts.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.Knowledge;
using Agentstration.Knowledge.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Security.Contracts;
using Agentstration.Tools;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class KnowledgeSourceApiTests : ModelManagementApiTestBase
{
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
        Assert.IsTrue(publicProperties.TryGetProperty("query", out _));
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
                JsonSerializer.SerializeToElement(new { knowledgeSourceId = "another-source", query = "override" }),
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
        string name)
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                knowledgeSourceId = new { type = "string" },
                operation = new { type = "string" },
                query = new { type = "string" }
            },
            required = new[] { "knowledgeSourceId", "operation", "query" },
            additionalProperties = false
        });
        var flows = services.GetRequiredService<FlowService>();
        var workspaceId = new WorkspaceId(context.WorkspaceId);
        await flows.CreateAsync(workspaceId, new CreateFlowCommand(
            name, null, "1.0.0", true,
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
            }), default, default);
        await flows.PublishVersionAsync(workspaceId, new(name), "1.0.0", true, default);
    }

    private static async Task CreatePublishedIngestionFlowAsync(
        IServiceProvider services,
        RequestContext context,
        string name,
        string artifactId)
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
        var output = JsonSerializer.SerializeToElement(new
        {
            artifacts = new[]
            {
                new { artifactId, kind = "staged", disposition = "publishable", name = "Documentation", mediaType = "text/markdown" }
            }
        });
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
}
