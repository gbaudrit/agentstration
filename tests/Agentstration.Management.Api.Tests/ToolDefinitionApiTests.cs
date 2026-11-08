using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ToolDefinitionApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    [DataRow("Instance")]
    [DataRow("Tenant")]
    public async Task AdministrativeFlowToolsPersistLogicalScopeSeparatelyFromOperationalPlacement(string scopeKind)
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var business = await GetBootstrapContextAsync(factory);
        var topology = factory.Services.GetRequiredService<IOperationalTopologyProvisioner>();
        var owner = scopeKind == "Instance" ? ResourceScopeRef.Instance : ResourceScopeRef.Tenant(business.TenantId);
        var operational = scopeKind == "Instance" ? await topology.GetInstanceOperationsWorkspaceAsync(default)
            : await topology.GetTenantOperationsWorkspaceAsync(business.TenantId, default);
        var context = new RequestContext(business.PrincipalId, operational.TenantId, operational.Id);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        using var administrativeScope = scopeKind == "Instance" ? scopes.PushInstance(context) : scopes.PushTenantAdministration(context);
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { message = new { type = "string" } }, required = new[] { "message" } });
        var flows = factory.Services.GetRequiredService<FlowService>();
        await flows.CreateExactAsync(owner, new("administrative-tool-flow", null, "2.4.0", true,
            GraphOnlyDefinition(), Graph: Graph(schema, schema)), ResourceNamespace.Default, default);
        await flows.PublishVersionExactAsync(owner, new("administrative-tool-flow"), "2.4.0", true, default);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Agentstration-Context", owner.Value);
        using var outsideLogicalScope = await client.PostAsJsonAsync("/api/tooldefinitions", new CreateToolDefinitionRequest("outside-logical-scope",
            Properties("Outside logical scope", "administrative-tool-flow", schema, schema) with
            { Publish = false, Flow = new() { Name = "administrative-tool-flow", ScopeRef = ResourceScopeRef.Workspace(business.WorkspaceId), Version = "2.4.0", UseActiveVersion = false } }, ScopeRef: owner));
        Assert.AreEqual(HttpStatusCode.Forbidden, outsideLogicalScope.StatusCode, await outsideLogicalScope.Content.ReadAsStringAsync());
        using var created = await client.PostAsJsonAsync("/api/tooldefinitions", new CreateToolDefinitionRequest("administrative-flow-tool",
            Properties("Administrative Flow Tool", "administrative-tool-flow", schema, schema) with
            { Version = "7.0.0", Publish = false, Flow = new() { Name = "administrative-tool-flow", ScopeRef = owner, Version = "2.4.0", UseActiveVersion = false } }, ScopeRef: owner));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
        Assert.AreEqual(owner, (await created.Content.ReadFromJsonAsync<ToolDefinitionResource>())?.ScopeRef);
        using var published = await client.PostAsJsonAsync("/api/tooldefinitions/administrative-flow-tool/versions", new PublishToolDefinitionRequest("7.0.0"));
        Assert.AreEqual(HttpStatusCode.Created, published.StatusCode, await published.Content.ReadAsStringAsync());
        using var executed = await client.PostAsJsonAsync($"/api/tools/agentstration.administrative-flow-tool.v7.0.0/run?scopeRef={Uri.EscapeDataString(owner.Value)}",
            new RunToolRequest(JsonSerializer.SerializeToElement(new { message = "logical-scope" }), ToolRunMode.Execute));
        Assert.AreEqual(HttpStatusCode.OK, executed.StatusCode, await executed.Content.ReadAsStringAsync());
        var result = await executed.Content.ReadFromJsonAsync<RunToolResponse>();
        Assert.AreEqual("logical-scope", result?.Output?.GetProperty("message").GetString());
        Assert.IsNotNull(result?.Receipt?.FlowRunId);
        var runScope = new FlowRunScope(context.TenantId, new(context.WorkspaceId), context.PrincipalId) { LogicalScopeRef = owner };
        var run = await factory.Services.GetRequiredService<FlowRunService>().GetAsync(result.Receipt.FlowRunId, runScope, default);
        Assert.IsNotNull(run);
        Assert.AreEqual(owner, run.Value.Scope.LogicalScopeRef);
        Assert.AreEqual(owner, run.Value.Scope.AccessScopeRef);
        Assert.AreEqual(owner, run.Value.DefinitionScopeRef);
        Assert.AreEqual(ResourceScopeRef.Workspace(operational.Id), run.Value.Scope.ScopeRef);
        Assert.AreNotEqual(business.WorkspaceId, run.Value.Scope.WorkspaceId.Value);
        var inUse = await Assert.ThrowsAsync<FlowValidationException>(() => flows.DeleteExactAsync(owner, new("administrative-tool-flow"), null, false, default));
        Assert.AreEqual("flow_in_use_by_tool_definition", inUse.Code);
    }

    [TestMethod]
    public async Task PublishedVersionsDoNotShareIdempotentFlowSubmissions()
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var contract = await CreatePublishedFlowAsync(factory.Services, context, "versioned-flow");
        var definitions = factory.Services.GetRequiredService<ToolDefinitionService>();
        var properties = Properties("Versioned Tool", "versioned-flow", contract.Input, contract.Output) with
        {
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { dryRun = new { type = "boolean" } } }),
            FixedArguments = JsonSerializer.SerializeToElement(new { message = "first" })
        };
        var draft = await definitions.PutAsync(Resource("versioned", ResourceScopeRef.Workspace(context.WorkspaceId), properties), null, true, default);
        await definitions.PutAsync(draft.Value with { Definition = properties with
        { Version = "1.1.0", FixedArguments = JsonSerializer.SerializeToElement(new { message = "second" }) } }, draft.ETag, false, default);
        var executor = factory.Services.GetRequiredService<IToolDefinitionExecutor>();
        var invocation = new ToolDefinitionInvocation(context.TenantId, new(context.WorkspaceId), context.PrincipalId,
            default, "versioned.v1.0.0", "same-call", null, JsonSerializer.SerializeToElement(new { }), ToolDefinitionCallerKind.Mcp);
        var first = await executor.ExecuteAsync(invocation, default);
        var second = await executor.ExecuteAsync(invocation with { ToolName = "versioned.v1.1.0" }, default);
        var replay = await executor.ExecuteAsync(invocation, default);
        Assert.AreEqual("first", first.Output?.GetProperty("message").GetString());
        Assert.AreEqual("second", second.Output?.GetProperty("message").GetString());
        Assert.AreNotEqual(first.Receipt.FlowRunId, second.Receipt.FlowRunId);
        Assert.AreEqual(first.Receipt.FlowRunId, replay.Receipt.FlowRunId);
        Assert.IsTrue(replay.Receipt.Recovered);
    }

    [TestMethod]
    public async Task ProtectedInputsAreRejectedBeforeDurableFlowSubmission()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var contract = await CreatePublishedFlowAsync(factory.Services, context, "protected-input-flow");
        await factory.Services.GetRequiredService<ToolDefinitionService>().PutAsync(Resource("protected-input",
            ResourceScopeRef.Workspace(context.WorkspaceId), Properties("Protected input", "protected-input-flow", contract.Input, contract.Output)), null, true, default);
        var failure = await Assert.ThrowsAsync<ToolDefinitionInvocationException>(() => factory.Services.GetRequiredService<IToolDefinitionExecutor>().ExecuteAsync(new(
            context.TenantId, new(context.WorkspaceId), context.PrincipalId, default, "protected-input.v1.0.0", "protected", null,
            JsonSerializer.SerializeToElement(new { message = "synthetic-sensitive-value" }), ToolDefinitionCallerKind.Mcp,
            ExecutionContext: new(HasSensitiveArguments: true)), default));
        Assert.AreEqual("tool_definition_protected_flow_input_unsupported", failure.Code);
        var runs = await factory.Services.GetRequiredService<IFlowRepository>().ListRunsAsync(
            new(context.TenantId, new(context.WorkspaceId), context.PrincipalId), null, null, 0, 100, default);
        Assert.IsFalse(runs.Items.Any(run => run.Value.FlowId.Value == "protected-input-flow"));
    }

    [TestMethod]
    public async Task FlowBackedToolDryRunReturnsPreviewWithoutCreatingWorkOrFlowRun()
    {
        await using var factory = Factory();
        var requestContext = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(requestContext);
        var contract = await CreatePublishedFlowAsync(factory.Services, requestContext, "dry-run-flow");
        await factory.Services.GetRequiredService<ToolDefinitionService>().PutAsync(Resource(
            "dry-run.preview",
            ResourceScopeRef.Workspace(requestContext.WorkspaceId),
            Properties("Dry run preview", "dry-run-flow", contract.Input, contract.Output)), null, true, default);
        var workspaceId = new WorkspaceId(requestContext.WorkspaceId);
        var flowScope = new FlowRunScope(requestContext.TenantId, workspaceId, requestContext.PrincipalId);
        var workRepository = factory.Services.GetRequiredService<IWorkItemRepository>();
        var flowRepository = factory.Services.GetRequiredService<IFlowRepository>();
        var workBefore = await workRepository.QueryAsync(new(workspaceId, requestContext.PrincipalId), default);
        var runsBefore = await flowRepository.ListRunsAsync(flowScope, null, null, 0, 100, default);

        using var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/tools/agentstration.dry-run.preview.v1.0.0/run",
            new RunToolRequest(JsonSerializer.SerializeToElement(new { message = "preview" })));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<RunToolResponse>();
        Assert.IsNotNull(result);
        Assert.IsTrue(result.ProviderInvoked);
        Assert.AreEqual(true, result.Output?.GetProperty("dryRun").GetBoolean());
        Assert.AreEqual(true, result.Output?.GetProperty("inputAccepted").GetBoolean());
        var workAfter = await workRepository.QueryAsync(new(workspaceId, requestContext.PrincipalId), default);
        var runsAfter = await flowRepository.ListRunsAsync(flowScope, null, null, 0, 100, default);
        Assert.AreEqual(workBefore.TotalCount, workAfter.TotalCount);
        Assert.AreEqual(runsBefore.Items.Count, runsAfter.Items.Count);
    }

    [TestMethod]
    public async Task ToolSimulationUsesGovernedPreviewWithoutExecutingOrCreatingAFlowRun()
    {
        var pipeline = new RecordingToolExecutionPipeline();
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IToolExecutionPipeline>();
            services.AddSingleton<IToolExecutionPipeline>(pipeline);
        }));
        var requestContext = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(requestContext);
        var contract = await CreatePublishedFlowAsync(factory.Services, requestContext, "preview-flow");
        using var client = factory.CreateClient();
        using var definition = await client.PostAsJsonAsync("/api/tooldefinitions", new CreateToolDefinitionRequest(
            "preview.run",
            Properties("Preview", "preview-flow", contract.Input, contract.Output)));
        Assert.AreEqual(HttpStatusCode.Created, definition.StatusCode);

        using var invalid = await client.PostAsJsonAsync(
            "/api/tools/agentstration.preview.run.v1.0.0/run",
            new RunToolRequest(JsonSerializer.SerializeToElement(new { })));
        Assert.AreEqual(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
        Assert.AreEqual(0, pipeline.Simulations);

        using var response = await client.PostAsJsonAsync(
            "/api/tools/agentstration.preview.run.v1.0.0/run",
            new RunToolRequest(JsonSerializer.SerializeToElement(new { message = "check" })));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<RunToolResponse>();
        Assert.IsNotNull(result);
        Assert.AreEqual(ToolRunMode.Simulate, result.Mode);
        Assert.AreEqual("simulated", result.Status);
        Assert.IsTrue(result.ProviderInvoked);
        Assert.AreEqual(true, result.Output?.GetProperty("dryRun").GetBoolean());
        Assert.AreEqual(1, pipeline.Simulations);
        Assert.AreEqual(0, pipeline.Executions);

        using var execution = await client.PostAsJsonAsync(
            "/api/tools/agentstration.preview.run.v1.0.0/run",
            new RunToolRequest(JsonSerializer.SerializeToElement(new { message = "execute" }), ToolRunMode.Execute));
        Assert.AreEqual(HttpStatusCode.OK, execution.StatusCode);
        var executionResult = await execution.Content.ReadFromJsonAsync<RunToolResponse>();
        Assert.IsNotNull(executionResult);
        Assert.AreEqual(ToolRunMode.Execute, executionResult.Mode);
        Assert.IsTrue(executionResult.ProviderInvoked);
        Assert.AreEqual(1, pipeline.Executions);
    }

    [TestMethod]
    public async Task ToolDefinitionCrudMaterializesInternalProviderAndGovernedTool()
    {
        await using var factory = Factory();
        var requestContext = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(requestContext);
        var contract = await CreatePublishedFlowAsync(factory.Services, requestContext, "notification-flow");
        using var client = factory.CreateClient();

        using var createdResponse = await client.PostAsJsonAsync("/api/tooldefinitions", new CreateToolDefinitionRequest(
            "notification.send",
            Properties("Send notification", "notification-flow", contract.Input, contract.Output)));

        Assert.AreEqual(HttpStatusCode.Created, createdResponse.StatusCode);
        Assert.IsNotNull(createdResponse.Headers.ETag);
        var created = await createdResponse.Content.ReadFromJsonAsync<ToolDefinitionResource>();
        Assert.IsNotNull(created);
        Assert.AreEqual(ResourceScopeRef.Workspace(requestContext.WorkspaceId), created.ScopeRef);

        var store = factory.Services.GetRequiredService<IResourceStore>();
        var workspaceScope = ResourceScopeRef.Workspace(requestContext.WorkspaceId);
        var providerAddress = ScopedResourceAddress.Create(workspaceScope, ResourceNamespace.Default,
            ToolResourceKinds.ToolProvider, AgentstrationToolProvider.Name);
        var toolAddress = ScopedResourceAddress.Create(workspaceScope, ResourceNamespace.Default,
            ToolResourceKinds.Tool, AgentstrationToolProvider.ToolResourceName(ToolDefinitionService.VersionName("notification.send", "1.0.0")));
        var provider = await store.GetExactAsync<ToolProviderResource>(providerAddress, default);
        var tool = await store.GetExactAsync<ToolResource>(toolAddress, default);
        Assert.IsNotNull(provider);
        Assert.IsTrue(provider.Value.Definition.Mcp?.Internal);
        Assert.IsNotNull(tool);
        Assert.AreEqual("notification.send.v1.0.0", tool.Value.Definition.ExternalId);
        Assert.AreEqual(created.Uid, tool.Value.Definition.Metadata["agentstration.toolDefinitionUid"].GetGuid());
        Assert.IsTrue(tool.Value.Definition.Enabled);

        using var update = new HttpRequestMessage(HttpMethod.Put, "/api/tooldefinitions/notification.send?namespace=default")
        {
            Content = JsonContent.Create(new PutToolDefinitionRequest(Properties("Send an alert", "notification-flow", contract.Input, contract.Output) with { Publish = false }))
        };
        update.Headers.IfMatch.Add(createdResponse.Headers.ETag!);
        using var updatedResponse = await client.SendAsync(update);
        Assert.AreEqual(HttpStatusCode.OK, updatedResponse.StatusCode);
        var updated = await updatedResponse.Content.ReadFromJsonAsync<ToolDefinitionResource>();
        Assert.AreEqual("Send an alert", updated!.Definition.DisplayName);

        using var disable = new HttpRequestMessage(HttpMethod.Put, "/api/tooldefinitions/notification.send/enabled?namespace=default")
        {
            Content = JsonContent.Create(new SetToolDefinitionEnabledRequest(false))
        };
        disable.Headers.IfMatch.Add(updatedResponse.Headers.ETag!);
        using var disabledResponse = await client.SendAsync(disable);
        Assert.AreEqual(HttpStatusCode.OK, disabledResponse.StatusCode);
        var disabled = await disabledResponse.Content.ReadFromJsonAsync<ToolDefinitionResource>();
        Assert.IsFalse(disabled!.Definition.Enabled);
        tool = await store.GetExactAsync<ToolResource>(toolAddress, default);
        Assert.IsTrue(tool!.Value.Definition.Enabled);
        await Assert.ThrowsAsync<ToolDefinitionInvocationException>(() => factory.Services.GetRequiredService<ToolDefinitionService>().GetInvocationAsync("notification.send.v1.0.0", default, default));

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/tooldefinitions/notification.send?namespace=default");
        delete.Headers.IfMatch.Add(disabledResponse.Headers.ETag!);
        using var deleted = await client.SendAsync(delete);
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.IsNull(await store.GetAsync<ToolDefinitionResource>(new(ToolResourceKinds.ToolDefinition, "notification.send"), default));
        Assert.IsNotNull(await store.GetExactAsync<ToolResource>(toolAddress, default));
    }

    [TestMethod]
    public async Task ToolDefinitionPinsSharedSchemaAndMaterializesItsEffectiveContract()
    {
        await using var factory = Factory();
        var requestContext = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(requestContext);
        var contract = await CreatePublishedFlowAsync(factory.Services, requestContext, "shared-contract-flow");
        var scope = ResourceScopeRef.Workspace(requestContext.WorkspaceId);
        var schemas = factory.Services.GetRequiredService<SchemaManagementService>();
        await schemas.CreateAsync(new SchemaResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = SchemaResourceKinds.Schema,
            Metadata = new() { Name = "tool-input" },
            ScopeRef = scope,
            Definition = new SchemaProperties
            {
                DisplayName = "Tool input",
                Format = SchemaFormatIds.JsonSchema,
                Dialect = "https://json-schema.org/draft/2020-12/schema",
                Content = contract.Input.Clone()
            }
        }, default);
        var published = await schemas.PublishAsync(scope, ResourceNamespace.Default, "tool-input", "1.0.0", default);
        var properties = Properties("Shared contract", "shared-contract-flow", contract.Input, contract.Output) with
        {
            InputSchemaBinding = new SchemaBinding
            {
                Reference = new()
                {
                    Name = "tool-input",
                    Version = "1.0.0",
                    ScopeRef = scope
                }
            }
        };

        var stored = await factory.Services.GetRequiredService<ToolDefinitionService>().PutAsync(
            Resource("shared.contract", scope, properties), null, true, default);

        Assert.AreEqual(published.Value.DefinitionHash, stored.Value.SchemaBindings["$.inputSchema"].DefinitionHash);
        Assert.AreEqual("1.0.0", stored.Value.SchemaBindings["$.inputSchema"].Source?.Version);
        var materialized = await factory.Services.GetRequiredService<IResourceStore>().GetAsync<ToolResource>(
            new(ToolResourceKinds.Tool, AgentstrationToolProvider.ToolResourceName(ToolDefinitionService.VersionName("shared.contract", "1.0.0"))), default);
        Assert.IsNotNull(materialized);
        Assert.IsTrue(ToolDefinitionService.SameSchema(contract.Input, materialized.Value.Definition.Schema?.Input));
    }

    private sealed class RecordingToolExecutionPipeline : IToolExecutionPipeline
    {
        public int Simulations { get; private set; }
        public int Executions { get; private set; }

        public ValueTask<JsonElement?> ExecuteAsync(ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            Executions++;
            return ValueTask.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { ok = true }));
        }

        public ValueTask<ToolExecutionSimulation> SimulateAsync(ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            Simulations++;
            Assert.AreEqual(ToolExecutionOwnerKind.Console, context.OwnerKind);
            Assert.IsNotNull(context.WorkspaceId);
            Assert.IsNotNull(context.PrincipalId);
            Assert.AreEqual(true, context.Arguments?.GetProperty("dryRun").GetBoolean());
            return ValueTask.FromResult(new ToolExecutionSimulation([], JsonSerializer.SerializeToElement(new { dryRun = true })));
        }
    }

    [TestMethod]
    public async Task ToolDefinitionRejectsIncompatibleContractAndBlocksIncompatibleActiveFlowVersion()
    {
        await using var factory = Factory();
        var requestContext = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(requestContext);
        var contract = await CreatePublishedFlowAsync(factory.Services, requestContext, "analysis-flow");
        var service = factory.Services.GetRequiredService<ToolDefinitionService>();
        var scope = ResourceScopeRef.Workspace(requestContext.WorkspaceId);
        var invalid = Resource("news.analyze", scope, Properties(
            "Analyze news",
            "analysis-flow",
            JsonSerializer.SerializeToElement(new { type = "object", required = new[] { "url" } }),
            contract.Output));

        var incompatible = await Assert.ThrowsAsync<ToolDefinitionValidationException>(async () =>
            await service.PutAsync(invalid, null, true, default));
        Assert.AreEqual("tool_definition_input_schema_incompatible", incompatible.Code);

        var stored = await service.PutAsync(Resource("news.analyze", scope, Properties("Analyze news", "analysis-flow", contract.Input, contract.Output)), null, true, default);
        Assert.AreEqual(1, stored.Value.Generation);
        var exact = Properties("Analyze news v1", "analysis-flow", contract.Input, contract.Output) with
        {
            Flow = new ToolDefinitionFlowTarget { Name = "analysis-flow", Version = "1.0.0", UseActiveVersion = false }
        };
        var exactStored = await service.PutAsync(Resource("news.analyze.v1", scope, exact), null, true, default);
        Assert.AreEqual("1.0.0", ((FlowToolDefinitionProperties)exactStored.Value.Definition).Flow.Version);

        var flows = factory.Services.GetRequiredService<FlowService>();
        var workspaceId = new WorkspaceId(requestContext.WorkspaceId);
        var current = await flows.GetAsync(workspaceId, new FlowId("analysis-flow"), default);
        Assert.IsNotNull(current);
        var deletion = await Assert.ThrowsAsync<FlowValidationException>(async () =>
            await flows.DeleteAsync(workspaceId, new FlowId("analysis-flow"), current.ETag, default));
        Assert.AreEqual("flow_in_use_by_tool_definition", deletion.Code);
        var changedInput = JsonSerializer.SerializeToElement(new { type = "object", required = new[] { "headline" }, properties = new { headline = new { type = "string" } } });
        var updated = await flows.UpdateAsync(workspaceId, new FlowId("analysis-flow"), new UpdateFlowCommand(
            current.Value.Description,
            "2.0.0",
            true,
            current.Value.Definition,
            current.Value.Metadata,
            Graph(changedInput, contract.Output)), current.ETag, default);

        var activation = await Assert.ThrowsAsync<FlowValidationException>(async () =>
            await flows.PublishVersionAsync(workspaceId, updated.Value.Id, "2.0.0", true, default));
        Assert.AreEqual("flow_tool_definition_contract_incompatible", activation.Code);
    }

    [TestMethod]
    public async Task FlowWithDistinctNamedSuccessContractsActivatesWithoutAToolDefinition()
    {
        await using var factory = Factory();
        var requestContext = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(requestContext);
        var flows = factory.Services.GetRequiredService<FlowService>();
        var workspaceId = new WorkspaceId(requestContext.WorkspaceId);
        var approved = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { decision = new { type = "string" } },
            required = new[] { "decision" }
        });
        var rejected = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { reason = new { type = "string" } },
            required = new[] { "reason" }
        });
        var graph = new FlowGraphDefinition
        {
            EntryStep = "input",
            Steps =
            [
                new InputFlowStepDefinition { Name = "input", DisplayName = "Input" },
                new ConditionFlowStepDefinition
                {
                    Name = "decision",
                    DisplayName = "Decision",
                    Left = "${input.approved}",
                    Operator = "equals",
                    Right = "true"
                },
                new OutputFlowStepDefinition
                {
                    Name = "approved",
                    DisplayName = "Approved",
                    Outcome = FlowOutputOutcome.Success,
                    OutputMapping = JsonSerializer.SerializeToElement("${transition.output}"),
                    Schema = approved
                },
                new OutputFlowStepDefinition
                {
                    Name = "rejected",
                    DisplayName = "Rejected",
                    Outcome = FlowOutputOutcome.Success,
                    OutputMapping = JsonSerializer.SerializeToElement("${transition.output}"),
                    Schema = rejected
                }
            ],
            Transitions =
            [
                new("input-decision", "input", "completed", "decision"),
                new("decision-approved", "decision", "true", "approved"),
                new("decision-rejected", "decision", "false", "rejected")
            ]
        };

        await flows.CreateAsync(workspaceId, new CreateFlowCommand(
            "named-output-flow",
            null,
            "1.0.0",
            true,
            GraphOnlyDefinition(),
            Graph: graph), ResourceNamespace.Default, default);

        var published = await flows.PublishVersionAsync(workspaceId,
            new FlowId("named-output-flow"), "1.0.0", true, default);

        Assert.AreEqual("1.0.0", published.Value.Version);
    }

    [TestMethod]
    public async Task EnabledDefinitionIsListedByMcpAndExecutesItsFlowIdempotently()
    {
        await using var factory = Factory();
        await using var worker = await AwpTestWorker.StartAsync(factory);
        var requestContext = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(requestContext);
        var contract = await CreatePublishedFlowAsync(factory.Services, requestContext, "echo-tool-flow");
        var definitions = factory.Services.GetRequiredService<ToolDefinitionService>();
        await definitions.PutAsync(Resource(
            "document.review",
            ResourceScopeRef.Workspace(requestContext.WorkspaceId),
            Properties("Review document", "echo-tool-flow", contract.Input, contract.Output)), null, true, default);

        var http = factory.CreateClient();
        using var consoleExecution = await http.PostAsJsonAsync(
            "/api/tools/agentstration.document.review.v1.0.0/run",
            new RunToolRequest(JsonSerializer.SerializeToElement(new { message = "via console" }), ToolRunMode.Execute));
        Assert.AreEqual(HttpStatusCode.OK, consoleExecution.StatusCode);
        var consoleResult = await consoleExecution.Content.ReadFromJsonAsync<RunToolResponse>();
        Assert.IsNotNull(consoleResult);
        Assert.IsTrue(consoleResult.ProviderInvoked);
        Assert.IsNotNull(consoleResult.Receipt?.WorkItemId);
        Assert.IsNotNull(consoleResult.Receipt?.FlowRunId);
        Assert.AreEqual("via console", consoleResult.Output?.GetProperty("message").GetString());

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "mcp"), Name = "agentstration-test" },
            http,
            NullLoggerFactory.Instance,
            ownsHttpClient: false);
        await using var mcp = await McpClient.CreateAsync(transport, loggerFactory: NullLoggerFactory.Instance);
        var published = await mcp.ListToolsAsync();
        var publishedTool = published.Single(value => value.Name == "document.review.v1.0.0");
        Assert.IsFalse(published.Any(value => value.Name is "flow.start" or "management.execute"));
        var mcpResult = await publishedTool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["message"] = "via mcp" }));
        StringAssert.Contains(JsonSerializer.Serialize(mcpResult), "via mcp");

        var assigned = await factory.Services.GetRequiredService<IToolCatalog>()
            .ResolveAsync([AgentstrationToolProvider.ToolResourceName(ToolDefinitionService.VersionName("document.review", "1.0.0"))]);
        var assignedTool = assigned.Single();
        Assert.AreEqual("document.review.v1.0.0", assignedTool.Name);
        Assert.AreEqual(AgentstrationToolProvider.Name, assignedTool.ProviderId);

        var arguments = JsonSerializer.SerializeToElement(new
        {
            message = "review this",
            workspaceId = Guid.NewGuid(),
            principalId = Guid.NewGuid()
        });
        var invocation = new ToolDefinitionInvocation(
            requestContext.TenantId,
            new WorkspaceId(requestContext.WorkspaceId),
            requestContext.PrincipalId,
            ResourceNamespace.Default,
            "document.review.v1.0.0",
            "logical-call-1",
            "correlation-1",
            arguments,
            ToolDefinitionCallerKind.Mcp);
        var executor = factory.Services.GetRequiredService<IToolDefinitionExecutor>();
        var invalidInput = await Assert.ThrowsAsync<ToolDefinitionInvocationException>(async () =>
            await executor.ExecuteAsync(invocation with { CallId = "invalid-call", Arguments = JsonSerializer.SerializeToElement(new { }) }, default));
        Assert.AreEqual("tool_definition_input_invalid", invalidInput.Code);
        var first = await executor.ExecuteAsync(invocation, default);
        var replay = await executor.ExecuteAsync(invocation, default);

        Assert.AreEqual(arguments.GetRawText(), first.Output?.GetRawText());
        Assert.AreEqual(first.Receipt.WorkItemId, replay.Receipt.WorkItemId);
        Assert.AreEqual(first.Receipt.FlowRunId, replay.Receipt.FlowRunId);
        Assert.AreEqual("1.0.0", first.Receipt.FlowVersion);
        Assert.IsFalse(first.Receipt.Recovered);
        Assert.IsTrue(replay.Receipt.Recovered);
        var persistedRun = await factory.Services.GetRequiredService<FlowRunService>().GetAsync(
            first.Receipt.FlowRunId ?? throw new AssertFailedException("Expected a Flow-backed receipt."),
            new FlowRunScope(requestContext.TenantId, new WorkspaceId(requestContext.WorkspaceId), requestContext.PrincipalId),
            default);
        Assert.IsNotNull(persistedRun);
        Assert.AreEqual(requestContext.WorkspaceId, persistedRun.Value.Scope.WorkspaceId.Value);
        Assert.AreEqual(requestContext.PrincipalId, persistedRun.Value.Scope.PrincipalId);
    }

    [TestMethod]
    public async Task ToolDefinitionNamespaceFlowsThroughMaterializedAgentAssignment()
    {
        await using var factory = Factory();
        var requestContext = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(requestContext);
        var ns = new ResourceNamespace("communications");
        var contract = await CreatePublishedFlowAsync(factory.Services, requestContext, "delivery", ns);
        var properties = Properties("Send notification", "delivery", contract.Input, contract.Output) with
        {
            Flow = new ToolDefinitionFlowTarget { Name = "delivery", Namespace = ns }
        };
        var resource = Resource("notification.send", ResourceScopeRef.Workspace(requestContext.WorkspaceId), properties) with
        {
            Metadata = new ResourceMetadata { Name = "notification.send", Namespace = ns }
        };
        await factory.Services.GetRequiredService<ToolDefinitionService>().PutAsync(resource, null, true, default);

        var assigned = await factory.Services.GetRequiredService<IToolCatalog>().ResolveAsync([
            ToolResourceIdentity.CatalogId(ns, AgentstrationToolProvider.ToolResourceName(ToolDefinitionService.VersionName("notification.send", "1.0.0")))
        ]);

        Assert.AreEqual(ns, assigned.Single().Namespace);
        Assert.AreEqual(ns, assigned.Single().ProviderNamespace);
    }

    private static ToolDefinitionResource Resource(string name, ResourceScopeRef scope, ToolDefinitionProperties properties) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ToolResourceKinds.ToolDefinition,
        Metadata = new ResourceMetadata { Name = name },
        ScopeRef = scope,
        Definition = properties
    };

    private static FlowToolDefinitionProperties Properties(string displayName, string flowName, JsonElement input, JsonElement output) => new()
    {
        DisplayName = displayName,
        Publish = true,
        InputSchema = input.Clone(),
        OutputSchema = output.Clone(),
        Flow = new ToolDefinitionFlowTarget { Name = flowName, UseActiveVersion = true }
    };

    private static async Task<(JsonElement Input, JsonElement Output)> CreatePublishedFlowAsync(
        IServiceProvider services,
        RequestContext context,
        string name,
        ResourceNamespace? resourceNamespace = null)
    {
        var input = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            required = new[] { "message" },
            properties = new
            {
                message = new { type = "string" },
                dryRun = new { type = "boolean" }
            }
        });
        var output = input.Clone();
        var flows = services.GetRequiredService<FlowService>();
        var workspaceId = new WorkspaceId(context.WorkspaceId);
        var ns = resourceNamespace ?? ResourceNamespace.Default;
        await flows.CreateAsync(workspaceId, new CreateFlowCommand(
            name,
            null,
            "1.0.0",
            true,
            GraphOnlyDefinition(),
            Graph: Graph(input, output)), ns, default);
        await flows.PublishVersionAsync(workspaceId, new FlowId(name, ns), "1.0.0", true, default);
        return (input, output);
    }

    private static WorkflowFlowDefinition GraphOnlyDefinition() => new(
        "input",
        [new FlowNode("input", FlowNodeKind.Input)],
        []);

    private static FlowGraphDefinition Graph(JsonElement input, JsonElement output) => new()
    {
        EntryStep = "input",
        InputSchema = input.Clone(),
        OutputSchema = output.Clone(),
        Steps =
        [
            new InputFlowStepDefinition { Name = "input", DisplayName = "Input", Schema = input.Clone() },
            new OutputFlowStepDefinition { Name = "output", DisplayName = "Output", OutputMapping = JsonSerializer.SerializeToElement("${input}") }
        ],
        Transitions = [new FlowTransitionDefinition("input-output", "input", "completed", "output")]
    };
}
