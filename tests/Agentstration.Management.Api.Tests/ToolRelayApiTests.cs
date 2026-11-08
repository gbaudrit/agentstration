using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text;
using Agentstration.Agents;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Notifications;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Parameters;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Agentstration.Tools;
using Agentstration.Tools.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed class ToolRelayApiTests : ModelManagementApiTestBase
{
    [TestMethod]
    [DataRow("Instance")]
    [DataRow("Tenant")]
    public async Task AdministrativeRelaysKeepLogicalOwnershipAndUseOnlyTheCorrespondingOperationalWorkspace(string scopeKind)
    {
        var handler = new SourceHandler();
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<IInternalMcpToolDefinitionProvider>(handler);
            services.AddSingleton<IInternalMcpToolHandler>(handler);
        }));
        var business = await GetBootstrapContextAsync(factory);
        var topology = factory.Services.GetRequiredService<IOperationalTopologyProvisioner>();
        var owner = scopeKind == "Instance" ? ResourceScopeRef.Instance : ResourceScopeRef.Tenant(business.TenantId);
        var operational = scopeKind == "Instance" ? await topology.GetInstanceOperationsWorkspaceAsync(default)
            : await topology.GetTenantOperationsWorkspaceAsync(business.TenantId, default);
        var context = new RequestContext(business.PrincipalId, operational.TenantId, operational.Id);
        var scopes = factory.Services.GetRequiredService<IRequestContextScopeFactory>();
        using var administrativeScope = scopeKind == "Instance" ? scopes.PushInstance(context) : scopes.PushTenantAdministration(context);
        handler.Context = factory.Services.GetRequiredService<ICurrentRequestContext>();
        var store = factory.Services.GetRequiredService<IResourceStore>();
        if (scopeKind == "Instance")
            await factory.Services.GetRequiredService<InternalMcpToolProjectionService>().EnsureAsync(owner, default, default);
        else
        {
            await store.PutExactAsync(owner, new ToolProviderResource
            {
                ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.ToolProvider, Metadata = new() { Name = AgentstrationToolProvider.Name }, ScopeRef = owner,
                Definition = new() { DisplayName = "Test internal provider", ProviderType = ToolProviderType.Mcp, Mcp = new() { Internal = true } }
            }, null, true, default);
            await store.PutExactAsync(owner, new ToolResource
            {
                ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.Tool, Metadata = new() { Name = "agentstration.relay-test.source" }, ScopeRef = owner,
                Definition = new()
                {
                    DisplayName = "Test source", ExternalId = handler.Definition.Name, Provider = new(AgentstrationToolProvider.Name, owner),
                    Schema = new() { Input = handler.Definition.InputSchema },
                    Discovery = new() { Available = true, FirstSeenAt = DateTimeOffset.UnixEpoch, LastSeenAt = DateTimeOffset.UnixEpoch }
                }
            }, null, true, default);
        }
        var source = (await store.GetExactAsync<ToolResource>(ScopedResourceAddress.Create(owner, ResourceNamespace.Default,
            ToolResourceKinds.Tool, "agentstration.relay-test.source"), default))!.Value;
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Agentstration-Context", owner.Value);
        using var created = await client.PostAsJsonAsync("/api/tooldefinitions", new CreateToolDefinitionRequest(
            "administrative-relay", Relay(source, "4.2.0", "administrative"), ScopeRef: owner));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
        var draft = await created.Content.ReadFromJsonAsync<ToolDefinitionResource>();
        Assert.AreEqual(owner, draft?.ScopeRef);
        using var invalidOwner = await client.PostAsJsonAsync("/api/tooldefinitions", new CreateToolDefinitionRequest(
            "wrong-operational-owner", Relay(source, "4.2.0", "administrative"), ScopeRef: ResourceScopeRef.Workspace(operational.Id)));
        Assert.AreEqual(HttpStatusCode.Forbidden, invalidOwner.StatusCode, await invalidOwner.Content.ReadAsStringAsync());
        using var published = await client.PostAsJsonAsync("/api/tooldefinitions/administrative-relay/versions", new PublishToolDefinitionRequest("4.2.0"));
        Assert.AreEqual(HttpStatusCode.Created, published.StatusCode, await published.Content.ReadAsStringAsync());
        using var executed = await client.PostAsJsonAsync($"/api/tools/agentstration.administrative-relay.v4.2.0/run?scopeRef={Uri.EscapeDataString(owner.Value)}",
            new RunToolRequest(Json("""{"query":"administrative"}"""), ToolRunMode.Execute));
        Assert.AreEqual(HttpStatusCode.OK, executed.StatusCode, await executed.Content.ReadAsStringAsync());
        var result = await executed.Content.ReadFromJsonAsync<RunToolResponse>();
        Assert.AreEqual(owner, result?.Receipt?.Relay?.SourceScopeRef);
        Assert.AreEqual(owner, handler.LastLogicalScope);
        Assert.AreEqual(operational.Id, handler.LastWorkspaceId);
        Assert.AreNotEqual(business.WorkspaceId, handler.LastWorkspaceId);
        Assert.AreEqual(1, handler.Calls);
        client.DefaultRequestHeaders.Remove("X-Agentstration-Context");
        client.DefaultRequestHeaders.Add("X-Agentstration-Context", ResourceScopeRef.Workspace(business.WorkspaceId).Value);
        using var wrongContext = await client.PostAsJsonAsync($"/api/tools/agentstration.administrative-relay.v4.2.0/run?scopeRef={Uri.EscapeDataString(owner.Value)}",
            new RunToolRequest(Json("""{"query":"wrong-context"}"""), ToolRunMode.Execute));
        Assert.AreEqual(HttpStatusCode.Forbidden, wrongContext.StatusCode);
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task ParameterChangesAndSecretRotationAreLiveAndProtectedWithoutRepublication()
    {
        var handler = new SourceHandler();
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<IInternalMcpToolDefinitionProvider>(handler);
            services.AddSingleton<IInternalMcpToolHandler>(handler);
        }));
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var scope = ResourceScopeRef.Workspace(context.WorkspaceId);
        var store = factory.Services.GetRequiredService<IResourceStore>();
        await factory.Services.GetRequiredService<InternalMcpToolProjectionService>().EnsureAsync(scope, default, default);
        var source = (await store.GetExactAsync<ToolResource>(new ResourceKey(ToolResourceKinds.Tool, "agentstration.relay-test.source").AtScope(scope), default))!.Value;
        var parameter = await factory.Services.GetRequiredService<ParameterManagementService>().CreateAsync(new ParameterResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = ParameterResourceKinds.Parameter,
            Metadata = new() { Name = "relay-store" }, ScopeRef = scope,
            Definition = new() { DisplayName = "Relay store", ValueType = ParameterValueType.Text, Value = Json("\"hr\"") }
        }, default);
        var secrets = factory.Services.GetRequiredService<SecretManagementService>();
        await secrets.CreateVaultAsync(new VaultResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = SecretResourceKinds.Vault,
            Metadata = new() { Name = "relay-vault" }, ScopeRef = scope,
            Definition = new() { DisplayName = "Relay vault", ProviderType = "local" }
        }, default);
        await secrets.InitializeVaultExactAsync(scope, "relay-vault", default);
        var secret = await secrets.CreateSecretAsync(new SecretResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = SecretResourceKinds.Secret,
            Metadata = new() { Name = "relay-secret" }, ScopeRef = scope,
            Definition = new() { DisplayName = "Relay secret", Vault = new("relay-vault", scope), Key = "fixture" }
        }, default);
        using (var value = new SecretValue(Encoding.UTF8.GetBytes("synthetic-first")))
            await secrets.SetValueExactAsync(scope, secret.Value.Name, value, default);
        var definitions = factory.Services.GetRequiredService<ToolDefinitionService>();
        var properties = Relay(source, "1.0.0", "unused") with
        {
            Publish = true,
            Bindings =
            [
                new ParameterToolArgumentBinding { Path = "/store/id", Parameter = new(parameter.Value.Address, scope) },
                new SecretToolArgumentBinding { Path = "/accessCode", Secret = new(secret.Value.Address, scope) }
            ]
        };
        await definitions.PutAsync(new ToolDefinitionResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.ToolDefinition,
            Metadata = new() { Name = "live-relay" }, ScopeRef = scope, Definition = properties
        }, null, true, default);
        var executor = factory.Services.GetRequiredService<IToolDefinitionExecutor>();
        var invocation = new ToolDefinitionInvocation(context.TenantId, new(context.WorkspaceId), context.PrincipalId,
            default, "live-relay.v1.0.0", "live", null, Json("""{"query":"a"}"""), ToolDefinitionCallerKind.Console);
        var first = await executor.ExecuteAsync(invocation, default);
        Assert.AreEqual("hr", first.Output?.GetProperty("store").GetProperty("id").GetString());
        Assert.AreEqual("synthetic-first", handler.LastSecret);
        await store.PutAsync(parameter.Value with { Generation = parameter.Value.Generation + 1,
            Definition = parameter.Value.Definition with { Value = Json("\"legal\"") } }, parameter.ETag, false, default);
        using (var value = new SecretValue(Encoding.UTF8.GetBytes("synthetic-second")))
            await secrets.SetValueExactAsync(scope, secret.Value.Name, value, default);
        var second = await executor.ExecuteAsync(invocation with { CallId = "rotated" }, default);
        Assert.AreEqual("legal", second.Output?.GetProperty("store").GetProperty("id").GetString());
        Assert.AreEqual("synthetic-second", handler.LastSecret);
        Assert.AreEqual(first.Receipt.Relay?.PublicationUid, second.Receipt.Relay?.PublicationUid);
        Assert.HasCount(1, await definitions.ListVersionsAsync("live-relay", default, default));
        var published = ResourceManifestSerializer.ToJson((await definitions.ListVersionsAsync("live-relay", default, default))[0]);
        Assert.IsFalse(published.Contains("synthetic-", StringComparison.Ordinal));
        Assert.IsFalse(first.Output?.GetRawText().Contains("synthetic-", StringComparison.Ordinal));
        Assert.IsTrue((await secrets.GetSecretUsagesAsync(scope, secret.Value.Name, default)).Any(usage => usage.Kind == ToolResourceKinds.ToolDefinitionVersion));
        await store.DeleteExactAsync(new ResourceKey(SecretResourceKinds.Secret, secret.Value.Name).AtScope(scope), secret.ETag, default);
        var missing = await Assert.ThrowsAsync<ToolDefinitionInvocationException>(() => executor.ExecuteAsync(invocation with { CallId = "missing" }, default));
        Assert.AreEqual("tool_definition_binding_unavailable", missing.Code);
        Assert.AreEqual(2, handler.Calls);
    }
    [TestMethod]
    public async Task RelayPublicationsRetainIndependentVersionsAndDoNotMutateConsumers()
    {
        var handler = new SourceHandler();
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<IInternalMcpToolDefinitionProvider>(handler);
            services.AddSingleton<IInternalMcpToolHandler>(handler);
        }));
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var scope = ResourceScopeRef.Workspace(context.WorkspaceId);
        await factory.Services.GetRequiredService<InternalMcpToolProjectionService>().EnsureAsync(scope, default, default);
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var source = (await store.GetExactAsync<ToolResource>(new ResourceKey(ToolResourceKinds.Tool, "agentstration.relay-test.source").AtScope(scope), default))!.Value;
        var properties = Relay(source, "1.0.0", "hr");
        using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync("/api/tooldefinitions", new CreateToolDefinitionRequest("relay-test", properties));
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
        var createdDefinition = await created.Content.ReadFromJsonAsync<ToolDefinitionResource>();
        Assert.IsNotNull(createdDefinition);
        Assert.IsNull(await store.GetExactAsync<ToolResource>(new ResourceKey(ToolResourceKinds.Tool, "agentstration.relay-test.v1.0.0").AtScope(scope), default));
        using var staleReview = await client.PostAsJsonAsync("/api/tooldefinitions/relay-test/versions", new PublishToolDefinitionRequest("1.0.0", "\"stale\""));
        Assert.AreEqual(HttpStatusCode.Conflict, staleReview.StatusCode);
        using var published = await client.PostAsJsonAsync("/api/tooldefinitions/relay-test/versions", new PublishToolDefinitionRequest("1.0.0"));
        Assert.AreEqual(HttpStatusCode.Created, published.StatusCode, await published.Content.ReadAsStringAsync());
        var firstTool = (await store.GetExactAsync<ToolResource>(new ResourceKey(ToolResourceKinds.Tool, "agentstration.relay-test.v1.0.0").AtScope(scope), default))!.Value;
        var agents = factory.Services.GetRequiredService<AgentManagementService>();
        var agent = await agents.PutAgentAsync(new AgentResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = AgentResourceKinds.Agent,
            Metadata = new() { Name = "direct-relay-consumer" }, ScopeRef = scope,
            Definition = new() { DisplayName = "Direct relay consumer", Instructions = "Use the assigned Tool.",
                ModelProfile = new("reasoning-default"), Tools = [new(firstTool.Name, scope)] }
        }, null, true, default);
        var deployment = new AgentDeploymentSpec
        { Environment = "local", RuntimeProfileName = "maf-builtin", HostingMode = AgentHostingMode.InProcess };
        var firstRevision = await agents.CreateRevisionAsync(agent.Value.Name, deployment, default);
        var flows = factory.Services.GetRequiredService<FlowService>();
        var workspaceId = new WorkspaceId(context.WorkspaceId);
        var flow = await flows.CreateAsync(workspaceId, new CreateFlowCommand("direct-relay-flow", null, "3.0.0", true,
            new WorkflowFlowDefinition("input", [new FlowNode("input", FlowNodeKind.Input)], []),
            Graph: RelayConsumerGraph(firstTool.Name)), default);
        await flows.PublishVersionAsync(workspaceId, flow.Value.Id, "3.0.0", true, default);
        var toolSets = factory.Services.GetRequiredService<ToolSetService>();
        await toolSets.CreateAsync(new ToolSetResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.ToolSet,
            Metadata = new() { Name = "relay-consumer" }, ScopeRef = scope,
            Definition = new() { DisplayName = "Relay consumer", Version = "4.0.0", Publish = true,
                Members = [new() { Tool = new(firstTool.Name, scope), Capability = "read", Route = "default" }] }
        }, default);
        var definitionService = factory.Services.GetRequiredService<ToolDefinitionService>();
        var draft = (await definitionService.GetAsync("relay-test", default, default))!;
        await definitionService.PutAsync(draft.Value with { Definition = Relay(source, "1.1.0", "legal") }, draft.ETag, false, default);
        await definitionService.PublishAsync("relay-test", default, "1.1.0", default);
        var retained = (await store.GetExactAsync<ToolResource>(new ResourceKey(ToolResourceKinds.Tool, firstTool.Name).AtScope(scope), default))!.Value;
        Assert.AreEqual(firstTool.Uid, retained.Uid);
        Assert.AreEqual(firstTool.Generation, retained.Generation);
        var selected = await toolSets.ResolveRouteAsync(default, "relay-consumer", "4.0.0", "read", "default", default);
        Assert.AreEqual(firstTool.Uid, selected.Member.ToolUid);
        Assert.AreEqual(firstTool.Generation, selected.Member.ToolGeneration);
        Assert.IsTrue((await definitionService.ListUsagesAsync("relay-test", default, default))
            .Any(usage => usage.Kind == "ToolSet" && usage.Version == "4.0.0" && usage.RelayVersion == "1.0.0"));
        var retainedRevision = await store.GetExactAsync<AgentRevision>(new ResourceKey(AgentResourceKinds.AgentRevision,
            firstRevision.Value.Name, firstRevision.Value.Namespace).AtScope(scope), default);
        Assert.IsNotNull(retainedRevision);
        Assert.AreEqual(firstRevision.Value.Uid, retainedRevision.Value.Uid);
        Assert.AreEqual(firstTool.Uid, retainedRevision.Value.Definition.ToolPins.Single().Uid);
        var retainedFlow = await flows.GetVersionAsync(workspaceId, flow.Value.Id, "3.0.0", default);
        Assert.IsNotNull(retainedFlow);
        Assert.AreEqual(firstTool.Name, retainedFlow.Value.Graph!.Steps.OfType<ToolFlowStepDefinition>().Single().Tool.ResourceId);
        var secondTool = (await store.GetExactAsync<ToolResource>(new ResourceKey(ToolResourceKinds.Tool,
            "agentstration.relay-test.v1.1.0").AtScope(scope), default))!.Value;
        await agents.PutAgentAsync(agent.Value with { Definition = agent.Value.Definition with { Tools = [new(secondTool.Name, scope)] } }, agent.ETag, false, default);
        var adoptedRevision = await agents.CreateRevisionAsync(agent.Value.Name, deployment, default);
        Assert.AreNotEqual(firstRevision.Value.Uid, adoptedRevision.Value.Uid);
        Assert.AreEqual(secondTool.Uid, adoptedRevision.Value.Definition.ToolPins.Single().Uid);
        var currentFlow = (await flows.GetAsync(workspaceId, flow.Value.Id, default))!;
        var updatedFlow = await flows.UpdateAsync(workspaceId, flow.Value.Id, new UpdateFlowCommand(null, "3.1.0", true,
            currentFlow.Value.Definition, Graph: RelayConsumerGraph(secondTool.Name)), currentFlow.ETag, default);
        await flows.PublishVersionAsync(workspaceId, updatedFlow.Value.Id, "3.1.0", true, default);
        var adoptedFlow = await flows.GetVersionAsync(workspaceId, flow.Value.Id, "3.1.0", default);
        Assert.IsNotNull(adoptedFlow);
        Assert.AreEqual(secondTool.Name, adoptedFlow.Value.Graph!.Steps.OfType<ToolFlowStepDefinition>().Single().Tool.ResourceId);
        var adoptionUsages = await definitionService.ListUsagesAsync("relay-test", default, default);
        Assert.IsTrue(adoptionUsages.Any(usage => usage.Kind == "Agent" && usage.RelayVersion == "1.0.0"));
        Assert.IsTrue(adoptionUsages.Any(usage => usage.Kind == "Agent" && usage.RelayVersion == "1.1.0"));
        Assert.IsTrue(adoptionUsages.Any(usage => usage.Kind == "Flow" && usage.Version == "3.0.0" && usage.RelayVersion == "1.0.0"));
        Assert.IsTrue(adoptionUsages.Any(usage => usage.Kind == "Flow" && usage.Version == "3.1.0" && usage.RelayVersion == "1.1.0"));
        var versions = await definitionService.ListVersionsAsync("relay-test", default, default);
        Assert.AreEqual(2, versions.Count);
        Assert.IsTrue(versions.All(version => version.Source?.Tool.Uid == source.Uid && version.Source.Tool.Generation == source.Generation));
        var executor = factory.Services.GetRequiredService<IToolDefinitionExecutor>();
        var invocation = new ToolDefinitionInvocation(context.TenantId, new(context.WorkspaceId), context.PrincipalId,
            default, "relay-test.v1.0.0", "first", "relay-test-correlation", Json("""{"query":"a"}"""), ToolDefinitionCallerKind.Console);
        var first = await executor.ExecuteAsync(invocation, default);
        var second = await executor.ExecuteAsync(invocation with { ToolName = "relay-test.v1.1.0", CallId = "second" }, default);
        Assert.AreEqual("hr", first.Output?.GetProperty("store").GetProperty("id").GetString());
        Assert.AreEqual("legal", second.Output?.GetProperty("store").GetProperty("id").GetString());
        Assert.AreEqual("1.0.0", first.Receipt.Relay?.RelayVersion);
        Assert.AreEqual(source.Uid, second.Receipt.Relay?.SourceToolUid);
        Assert.IsNull(first.Receipt.FlowRunId);
        Assert.AreEqual(2, handler.Calls);
        var latestDraft = (await definitionService.GetAsync("relay-test", default, default))!;
        await definitionService.PutAsync(latestDraft.Value with { Definition = Relay(source, "1.1.0", "changed") }, latestDraft.ETag, false, default);
        var immutable = await Assert.ThrowsAsync<ToolDefinitionValidationException>(() => definitionService.PublishAsync("relay-test", default, "1.1.0", default));
        Assert.AreEqual("tool_definition_version_immutable", immutable.Code);
        var overridden = await Assert.ThrowsAsync<ToolDefinitionInvocationException>(() => executor.ExecuteAsync(invocation with
        { Arguments = Json("""{"query":"a","store":{"id":"other"}}""") }, default));
        Assert.AreEqual("tool_definition_fixed_argument_override", overridden.Code);
        Assert.AreEqual(2, handler.Calls);
        using var executed = await client.PostAsJsonAsync("/api/tools/agentstration.relay-test.v1.0.0/run",
            new RunToolRequest(Json("""{"query":"a"}"""), ToolRunMode.Execute));
        Assert.AreEqual(HttpStatusCode.OK, executed.StatusCode, await executed.Content.ReadAsStringAsync());
        var execution = await executed.Content.ReadFromJsonAsync<RunToolResponse>();
        Assert.AreEqual("1.0.0", execution?.Receipt?.Relay?.RelayVersion);
        Assert.AreEqual(source.Uid, execution?.Receipt?.Relay?.SourceToolUid);
        Assert.IsNull(execution?.Receipt?.FlowRunId);
    }

    private static FlowGraphDefinition RelayConsumerGraph(string toolName) => new()
    {
        EntryStep = "input",
        InputSchema = Json("""{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}"""),
        Steps =
        [
            new InputFlowStepDefinition { Name = "input" },
            new ToolFlowStepDefinition { Name = "invoke", Tool = new(toolName), ArgumentsMapping = Json("""{"query":"${input.query}"}""") },
            new OutputFlowStepDefinition { Name = "output", OutputMapping = Json("\"${steps.invoke.output}\"") }
        ],
        Transitions = [new("input-tool", "input", "completed", "invoke"), new("tool-output", "invoke", "success", "output")]
    };

    [TestMethod]
    public async Task RelayRejectsStaleTargetsAndHonorsSourceHooks()
    {
        var handler = new SourceHandler();
        await using var factory = Factory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton<IInternalMcpToolDefinitionProvider>(handler);
            services.AddSingleton<IInternalMcpToolHandler>(handler);
        }));
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var scope = ResourceScopeRef.Workspace(context.WorkspaceId);
        await factory.Services.GetRequiredService<InternalMcpToolProjectionService>().EnsureAsync(scope, default, default);
        var store = factory.Services.GetRequiredService<IResourceStore>();
        var source = (await store.GetExactAsync<ToolResource>(new ResourceKey(ToolResourceKinds.Tool, "agentstration.relay-test.source").AtScope(scope), default))!;
        var definitions = factory.Services.GetRequiredService<ToolDefinitionService>();
        await definitions.PutAsync(new ToolDefinitionResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.ToolDefinition,
            Metadata = new() { Name = "guarded-relay" }, ScopeRef = scope,
            Definition = Relay(source.Value, "1.0.0", "hr") with { Publish = true }
        }, null, true, default);
        await store.PutAsync(new ToolExecutionHookResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = ToolResourceKinds.ToolExecutionHook,
            Metadata = new() { Name = "deny-source" }, ScopeRef = scope,
            Definition = new() { DisplayName = "Deny source", Handler = ToolExecutionHookHandlers.Deny,
                Selector = new() { Tools = [source.Value.Name] },
                Configuration = new Dictionary<string, JsonElement> { ["code"] = Json("\"blocked_source\""), ["message"] = Json("\"Source denied\"") } }
        }, null, true, default);
        var executor = factory.Services.GetRequiredService<IToolDefinitionExecutor>();
        var invocation = new ToolDefinitionInvocation(context.TenantId, new(context.WorkspaceId), context.PrincipalId,
            default, "guarded-relay.v1.0.0", "guarded", null, Json("""{"query":"a"}"""), ToolDefinitionCallerKind.Console);
        await Assert.ThrowsAsync<ToolExecutionDeniedException>(() => executor.ExecuteAsync(invocation, default));
        Assert.AreEqual(0, handler.Calls);
        using (factory.Services.GetRequiredService<IResourceGovernanceContext>().Push(ResourceGovernance.Core("tools.internal-mcp")))
            await store.PutAsync(source.Value with { Generation = source.Value.Generation + 1 }, source.ETag, false, default);
        var stale = await Assert.ThrowsAsync<ToolDefinitionInvocationException>(() => executor.ExecuteAsync(invocation, default));
        Assert.AreEqual("tool_definition_target_stale", stale.Code);
        Assert.AreEqual(0, handler.Calls);
        var foreign = context with { WorkspaceId = Guid.NewGuid() };
        using var foreignScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(foreign);
        await Assert.ThrowsAsync<Exception>(() => executor.ExecuteAsync(invocation with { WorkspaceId = new(foreign.WorkspaceId) }, default));
        Assert.AreEqual(0, handler.Calls);
    }

    private static RelayToolDefinitionProperties Relay(ToolResource source, string version, string store) => new()
    {
        DisplayName = "Test relay", Version = version,
        Target = new() { Name = source.Name, Namespace = source.Namespace, ScopeRef = source.ScopeRef!.Value, Uid = source.Uid, Generation = source.Generation },
        Bindings = [new LiteralToolArgumentBinding { Path = "/store", Value = JsonSerializer.SerializeToElement(new { id = store }) }]
    };
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    private sealed class SourceHandler : IInternalMcpToolHandler
    {
        public ICurrentRequestContext? Context { get; set; }
        public ResourceScopeRef? LastLogicalScope { get; private set; }
        public Guid? LastWorkspaceId { get; private set; }
        public int Calls { get; private set; }
        public string? LastSecret { get; private set; }
        public InternalMcpToolDefinition Definition { get; } = new("relay-test.source", "Test source", null,
            Json("""{"type":"object","properties":{"store":{"type":"object","properties":{"id":{"type":"string"}},"required":["id"],"additionalProperties":false},"query":{"type":"string"},"accessCode":{"type":"string"}},"required":["store","query"],"additionalProperties":false}"""));
        public Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastWorkspaceId = invocation.WorkspaceId.Value;
            LastLogicalScope = Context?.AccessMode switch
            {
                ControlPlaneAccessMode.Instance => ResourceScopeRef.Instance,
                ControlPlaneAccessMode.TenantAdministration => ResourceScopeRef.Tenant(invocation.TenantId),
                ControlPlaneAccessMode.Workspace => ResourceScopeRef.Workspace(invocation.WorkspaceId.Value),
                _ => null
            };
            LastSecret = invocation.Arguments.TryGetProperty("accessCode", out var secret) ? secret.GetString() : null;
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new
            { store = invocation.Arguments.GetProperty("store"), query = invocation.Arguments.GetProperty("query") }));
        }
    }
}
