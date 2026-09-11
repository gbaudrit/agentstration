using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Application;
using Agentstration.Infrastructure.Declarative;
using Agentstration.Infrastructure.Notifications;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Resources;
using Agentstration.Work;
using Agentstration.Work.Storage.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace Agentstration.Management.Tests;

public sealed partial class ModelManagementApiTests
{
    [TestMethod]
    public void NotificationDeliverySamplesUseOnlyGenericFlowAndToolSteps()
    {
        var sampleRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "notification-delivery"));
        var delivery = ResourceManifestSerializer.FromYaml<DeclarativeResourceEnvelope<DeclarativeFlowDefinition>>(
            File.ReadAllText(Path.Combine(sampleRoot, "flows", "notification-delivery.yaml")));
        var parent = ResourceManifestSerializer.FromYaml<DeclarativeResourceEnvelope<DeclarativeFlowDefinition>>(
            File.ReadAllText(Path.Combine(sampleRoot, "flows", "news-alert-parent.yaml")));
        var definition = ResourceManifestSerializer.FromYaml<ToolDefinitionResource>(
            File.ReadAllText(Path.Combine(sampleRoot, "tooldefinitions", "notification-send.yaml")));

        Assert.IsInstanceOfType<ToolFlowStepDefinition>(delivery.Definition.Graph!.Steps.Single(value => value.Name == "create-notification"));
        Assert.IsInstanceOfType<FlowCallStepDefinition>(parent.Definition.Graph!.Steps.Single(value => value.Name == "deliver"));
        Assert.AreEqual("notification-delivery", definition.Definition.Flow.Name);
        Assert.IsTrue(definition.Definition.Flow.UseActiveVersion);
    }

    [TestMethod]
    public async Task InternalNotificationToolIsPublishedAndExplicitDeliveryKeyIsIdempotent()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var http = factory.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "mcp"), Name = "notification-test" },
            http,
            NullLoggerFactory.Instance,
            ownsHttpClient: false);
        await using var mcp = await McpClient.CreateAsync(transport, loggerFactory: NullLoggerFactory.Instance);
        var tool = (await mcp.ListToolsAsync()).Single(value => value.Name == AgentstrationInternalTools.NotificationCreate);
        var arguments = new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["deliveryKey"] = "daily-news-2026-09-10",
            ["title"] = "Daily news",
            ["message"] = "A new article is ready."
        });

        await tool.InvokeAsync(arguments);
        await tool.InvokeAsync(arguments);

        var notifications = await factory.Services.GetRequiredService<IWorkplaceRepository>()
            .ListNotificationsAsync(new WorkspaceId(context.WorkspaceId), null, default);
        Assert.HasCount(1, notifications);
        Assert.AreEqual("daily-news-2026-09-10", notifications[0].DeliveryKey);
        Assert.AreEqual(context.WorkspaceId, notifications[0].WorkspaceId.Value);
        var handler = factory.Services.GetRequiredService<IInternalMcpToolHandler>();
        var spoof = await Assert.ThrowsAsync<ToolDefinitionInvocationException>(async () => await handler.ExecuteAsync(new(
            context.TenantId,
            new WorkspaceId(context.WorkspaceId),
            context.PrincipalId,
            "spoof-call",
            null,
            JsonSerializer.SerializeToElement(new { deliveryKey = "spoof", title = "Spoof", message = "Spoof", workspaceId = Guid.NewGuid() }),
            ToolDefinitionCallerKind.Mcp), default));
        Assert.AreEqual("notification_argument_unknown", spoof.Code);
        var projected = await factory.Services.GetRequiredService<IControlPlaneStore>().GetAsync<ToolResource>(
            new(ResourceKinds.Tool, AgentstrationToolProvider.ToolResourceName(AgentstrationInternalTools.NotificationCreate)), default);
        Assert.IsNotNull(projected);
        Assert.AreEqual(AgentstrationInternalTools.NotificationCreate, projected.Value.Definition.ExternalId);
        var reserved = await Assert.ThrowsAsync<ToolDefinitionValidationException>(async () => await factory.Services
            .GetRequiredService<ToolDefinitionService>()
            .PutAsync(NotificationToolDefinition(
                AgentstrationInternalTools.NotificationCreate,
                "unused",
                ResourceScopeRef.Workspace(context.WorkspaceId),
                NotificationSchemas()), null, true, default));
        Assert.AreEqual("tool_definition_name_reserved", reserved.Code);
    }

    [TestMethod]
    public async Task ParentAndToolDefinitionReuseDeliveryFlowWithoutNotificationRecursion()
    {
        await using var factory = Factory();
        var context = await GetBootstrapContextAsync(factory);
        using var requestScope = factory.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
        var workspaceId = new WorkspaceId(context.WorkspaceId);
        var scope = ResourceScopeRef.Workspace(context.WorkspaceId);
        await factory.Services.GetRequiredService<InternalMcpToolProjectionService>()
            .EnsureAsync(scope, ResourceNamespace.Default, default);
        var schemas = NotificationSchemas();
        var flows = factory.Services.GetRequiredService<FlowService>();

        await CreateAndPublishAsync(flows, workspaceId, "notification-delivery", "1.0.0", DeliveryGraph(schemas.Input, schemas.Output), true);
        await CreateAndPublishAsync(flows, workspaceId, "news-parent", "1.0.0", ParentGraph(schemas.Input, schemas.Output), true);
        var definitions = factory.Services.GetRequiredService<ToolDefinitionService>();
        await definitions.PutAsync(NotificationToolDefinition("notification.send", "notification-delivery", scope, schemas), null, true, default);
        await definitions.PutAsync(NotificationToolDefinition("news.alert", "news-parent", scope, schemas), null, true, default);
        var arguments = JsonSerializer.SerializeToElement(new
        {
            deliveryKey = "article-42",
            title = "News detected",
            message = "Article 42 requires review.",
            actionUrl = "/tasks/article-42"
        });
        var executor = factory.Services.GetRequiredService<IToolDefinitionExecutor>();
        var invocation = new ToolDefinitionInvocation(
            context.TenantId,
            workspaceId,
            context.PrincipalId,
            ResourceNamespace.Default,
            "news.alert",
            "news-alert-call-1",
            "news-correlation-1",
            arguments,
            ToolDefinitionCallerKind.Mcp);

        var first = await executor.ExecuteAsync(invocation, default);
        var replay = await executor.ExecuteAsync(invocation, default);

        Assert.IsFalse(first.Receipt.Recovered);
        Assert.IsTrue(replay.Receipt.Recovered);
        Assert.AreEqual(first.Receipt.FlowRunId, replay.Receipt.FlowRunId);
        var root = await factory.Services.GetRequiredService<FlowRunService>().GetAsync(
            first.Receipt.FlowRunId,
            new FlowRunScope(context.TenantId, workspaceId, context.PrincipalId),
            default);
        Assert.IsNotNull(root);
        var childId = root.Value.Steps.Single(value => value.StepName == "deliver").ChildFlowRunId;
        Assert.IsNotNull(childId);
        var delivery = await factory.Services.GetRequiredService<FlowRunService>().GetAsync(
            childId,
            new FlowRunScope(context.TenantId, workspaceId, context.PrincipalId),
            default);
        Assert.IsNotNull(delivery);
        Assert.AreEqual("notification-delivery", delivery.Value.FlowId.Value);
        Assert.AreEqual("1.0.0", delivery.Value.FlowVersion);
        Assert.AreEqual(arguments.GetRawText(), delivery.Value.Input.GetRawText());
        var notification = (await factory.Services.GetRequiredService<IWorkplaceRepository>()
            .ListNotificationsAsync(workspaceId, null, default)).Single();
        Assert.AreEqual(delivery.Value.Id, notification.SourceRunId);
        Assert.AreEqual("create-notification", notification.SourceStepId);
        StringAssert.Contains(notification.SourceToolCallId, "step:create-notification");

        var current = await flows.GetAsync(workspaceId, new FlowId("notification-delivery"), default);
        Assert.IsNotNull(current);
        await AddExternalDeliveryToolAsync(factory.Services.GetRequiredService<IControlPlaneStore>(), scope, schemas);
        var updated = await flows.UpdateAsync(workspaceId, current.Value.Id, new UpdateFlowCommand(
            current.Value.Description,
            "2.0.0",
            true,
            current.Value.Definition,
            current.Value.Metadata,
            DeliveryGraph(schemas.Input, schemas.Output, "third-party.messages.send")), current.ETag, default);
        await flows.PublishVersionAsync(workspaceId, updated.Value.Id, "2.0.0", true, default);
        var parent = await flows.GetAsync(workspaceId, new FlowId("news-parent"), default);
        Assert.AreEqual(FlowCallVersionStrategy.Active, Assert.IsInstanceOfType<FlowCallStepDefinition>(parent!.Value.Graph!.Steps.Single(value => value.Name == "deliver")).Flow.VersionStrategy);
    }

    private static async Task CreateAndPublishAsync(FlowService flows, WorkspaceId workspaceId, string name, string version, FlowGraphDefinition graph, bool activate)
    {
        await flows.CreateAsync(workspaceId, new CreateFlowCommand(
            name,
            null,
            version,
            true,
            new DirectFlowDefinition(new FlowTargetReference(FlowTargetKind.Agent, "unused")),
            Graph: graph), ResourceNamespace.Default, default);
        await flows.PublishVersionAsync(workspaceId, new FlowId(name), version, activate, default);
    }

    private static ToolDefinitionResource NotificationToolDefinition(
        string name,
        string flow,
        ResourceScopeRef scope,
        (JsonElement Input, JsonElement Output) schemas) => new()
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.ToolDefinition,
            Metadata = new ResourceMetadata { Name = name },
            ScopeRef = scope,
            Definition = new ToolDefinitionProperties
            {
                DisplayName = name,
                InputSchema = schemas.Input.Clone(),
                OutputSchema = schemas.Output.Clone(),
                Flow = new ToolDefinitionFlowTarget { Name = flow, UseActiveVersion = true }
            }
        };

    private static FlowGraphDefinition DeliveryGraph(JsonElement input, JsonElement output, string? toolResourceId = null) => new()
    {
        EntryStep = "input",
        InputSchema = input.Clone(),
        OutputSchema = output.Clone(),
        Steps =
        [
            new InputFlowStepDefinition { Name = "input", Schema = input.Clone() },
            new ToolFlowStepDefinition
            {
                Name = "create-notification",
                Tool = new(toolResourceId ?? AgentstrationToolProvider.ToolResourceName(AgentstrationInternalTools.NotificationCreate)),
                ArgumentsMapping = JsonSerializer.SerializeToElement(new
                {
                    deliveryKey = "${input.deliveryKey}",
                    title = "${input.title}",
                    message = "${input.message}",
                    actionUrl = "${input.actionUrl}"
                })
            },
            new OutputFlowStepDefinition { Name = "output", OutputMapping = JsonSerializer.SerializeToElement("${steps.create-notification.output}") }
        ],
        Transitions =
        [
            new("input-tool", "input", "completed", "create-notification"),
            new("tool-output", "create-notification", "completed", "output")
        ]
    };

    private static FlowGraphDefinition ParentGraph(JsonElement input, JsonElement output) => new()
    {
        EntryStep = "input",
        InputSchema = input.Clone(),
        OutputSchema = output.Clone(),
        Steps =
        [
            new InputFlowStepDefinition { Name = "input", Schema = input.Clone() },
            new FlowCallStepDefinition { Name = "deliver", Flow = new("notification-delivery"), InputMapping = JsonSerializer.SerializeToElement("${input}") },
            new OutputFlowStepDefinition { Name = "output", OutputMapping = JsonSerializer.SerializeToElement("${steps.deliver.output}") }
        ],
        Transitions = [new("input-delivery", "input", "completed", "deliver"), new("delivery-output", "deliver", "completed", "output")]
    };

    private static (JsonElement Input, JsonElement Output) NotificationSchemas()
    {
        var input = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                deliveryKey = new { type = "string" },
                title = new { type = "string" },
                message = new { type = "string" },
                actionUrl = new { type = "string" }
            },
            required = new[] { "deliveryKey", "title", "message" }
        });
        var output = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                notificationId = new { type = "string" },
                deliveryKey = new { type = "string" },
                createdAt = new { type = "string" },
                recovered = new { type = "boolean" }
            },
            required = new[] { "notificationId", "deliveryKey", "createdAt", "recovered" }
        });
        return (input, output);
    }

    private static async Task AddExternalDeliveryToolAsync(
        IControlPlaneStore store,
        ResourceScopeRef scope,
        (JsonElement Input, JsonElement Output) schemas)
    {
        var now = DateTimeOffset.UnixEpoch;
        await store.PutAsync(new ToolProviderResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.ToolProvider,
            Metadata = new ResourceMetadata { Name = "third-party" },
            ScopeRef = scope,
            Definition = new ToolProviderProperties
            {
                DisplayName = "Third-party MCP",
                ProviderType = ToolProviderType.Mcp,
                Mcp = new McpToolProviderConfiguration { Transport = McpToolProviderTransport.StreamableHttp, Endpoint = new Uri("http://127.0.0.1:9/mcp") }
            }
        }, null, true, default);
        await store.PutAsync(new ToolResource
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.Tool,
            Metadata = new ResourceMetadata { Name = "third-party.messages.send" },
            ScopeRef = scope,
            Definition = new ToolResourceProperties
            {
                DisplayName = "Send external message",
                Provider = new ResourceReference("third-party"),
                ExternalId = "messages.send",
                Enabled = true,
                Discovery = new ToolDiscoveryState { Available = true, FirstSeenAt = now, LastSeenAt = now },
                Schema = new ToolSchema { Input = schemas.Input.Clone(), Output = schemas.Output.Clone() }
            }
        }, null, true, default);
    }
}
