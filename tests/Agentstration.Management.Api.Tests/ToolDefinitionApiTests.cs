using Agentstration.Tools;
using Agentstration.ResourceManagement;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ToolDefinitionApiTests : ModelManagementApiTestBase
{
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
        var provider = await store.GetAsync<ToolProviderResource>(new(ResourceKinds.ToolProvider, AgentstrationToolProvider.Name), default);
        var tool = await store.GetAsync<ToolResource>(new(ResourceKinds.Tool, AgentstrationToolProvider.ToolResourceName("notification.send")), default);
        Assert.IsNotNull(provider);
        Assert.IsTrue(provider.Value.Definition.Mcp?.Internal);
        Assert.IsNotNull(tool);
        Assert.AreEqual("notification.send", tool.Value.Definition.ExternalId);
        Assert.AreEqual(created.Uid, tool.Value.Definition.Metadata["agentstration.toolDefinitionUid"].GetGuid());
        Assert.IsTrue(tool.Value.Definition.Enabled);

        using var update = new HttpRequestMessage(HttpMethod.Put, "/api/tooldefinitions/notification.send?namespace=default")
        {
            Content = JsonContent.Create(new PutToolDefinitionRequest(Properties("Send an alert", "notification-flow", contract.Input, contract.Output)))
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
        tool = await store.GetAsync<ToolResource>(new(ResourceKinds.Tool, AgentstrationToolProvider.ToolResourceName("notification.send")), default);
        Assert.IsFalse(tool!.Value.Definition.Enabled);

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/tooldefinitions/notification.send?namespace=default");
        delete.Headers.IfMatch.Add(disabledResponse.Headers.ETag!);
        using var deleted = await client.SendAsync(delete);
        Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.IsNull(await store.GetAsync<ToolDefinitionResource>(new(ResourceKinds.ToolDefinition, "notification.send"), default));
        Assert.IsNull(await store.GetAsync<ToolResource>(new(ResourceKinds.Tool, AgentstrationToolProvider.ToolResourceName("notification.send")), default));
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
        Assert.AreEqual("1.0.0", exactStored.Value.Definition.Flow.Version);

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
    public async Task EnabledDefinitionIsListedByMcpAndExecutesItsFlowIdempotently()
    {
        await using var factory = Factory();
        var requestContext = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(requestContext);
        var contract = await CreatePublishedFlowAsync(factory.Services, requestContext, "echo-tool-flow");
        var definitions = factory.Services.GetRequiredService<ToolDefinitionService>();
        await definitions.PutAsync(Resource(
            "document.review",
            ResourceScopeRef.Workspace(requestContext.WorkspaceId),
            Properties("Review document", "echo-tool-flow", contract.Input, contract.Output)), null, true, default);

        var http = factory.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "mcp"), Name = "agentstration-test" },
            http,
            NullLoggerFactory.Instance,
            ownsHttpClient: false);
        await using var mcp = await McpClient.CreateAsync(transport, loggerFactory: NullLoggerFactory.Instance);
        var published = await mcp.ListToolsAsync();
        var publishedTool = published.Single(value => value.Name == "document.review");
        Assert.IsFalse(published.Any(value => value.Name is "flow.start" or "management.execute"));
        var mcpResult = await publishedTool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["message"] = "via mcp" }));
        StringAssert.Contains(JsonSerializer.Serialize(mcpResult), "via mcp");

        var assigned = await factory.Services.GetRequiredService<IToolCatalog>()
            .ResolveAsync([AgentstrationToolProvider.ToolResourceName("document.review")]);
        var assignedTool = assigned.Single();
        Assert.AreEqual("document.review", assignedTool.Name);
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
            "document.review",
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
            first.Receipt.FlowRunId,
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
            ToolResourceIdentity.CatalogId(ns, AgentstrationToolProvider.ToolResourceName("notification.send"))
        ]);

        Assert.AreEqual(ns, assigned.Single().Namespace);
        Assert.AreEqual(ns, assigned.Single().ProviderNamespace);
    }

    private static ToolDefinitionResource Resource(string name, ResourceScopeRef scope, ToolDefinitionProperties properties) => new()
    {
        ApiVersion = ManagementApiVersions.CoreV1,
        Kind = ResourceKinds.ToolDefinition,
        Metadata = new ResourceMetadata { Name = name },
        ScopeRef = scope,
        Definition = properties
    };

    private static ToolDefinitionProperties Properties(string displayName, string flowName, JsonElement input, JsonElement output) => new()
    {
        DisplayName = displayName,
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
        var input = JsonSerializer.SerializeToElement(new { type = "object", required = new[] { "message" }, properties = new { message = new { type = "string" } } });
        var output = input.Clone();
        var flows = services.GetRequiredService<FlowService>();
        var workspaceId = new WorkspaceId(context.WorkspaceId);
        var ns = resourceNamespace ?? ResourceNamespace.Default;
        await flows.CreateAsync(workspaceId, new CreateFlowCommand(
            name,
            null,
            "1.0.0",
            true,
            new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "unused")),
            Graph: Graph(input, output)), ns, default);
        await flows.PublishVersionAsync(workspaceId, new FlowId(name, ns), "1.0.0", true, default);
        return (input, output);
    }

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
