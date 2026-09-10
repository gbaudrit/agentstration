using System.Text.Json;
using Agentstration.Application.Work;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Resources;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Notifications;

public sealed class WorkNotificationMcpToolDefinitionProvider : IInternalMcpToolDefinitionProvider
{
    public InternalMcpToolDefinition Definition { get; } = new(
        AgentstrationInternalTools.NotificationCreate,
        "Create in-product notification",
        "Creates one durable notification in the current Agentstration Workspace.",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                deliveryKey = new { type = "string", maxLength = 256 },
                title = new { type = "string", maxLength = 200 },
                message = new { type = "string", maxLength = 4000 },
                actionUrl = new { type = "string", maxLength = 2048 }
            },
            required = new[] { "deliveryKey", "title", "message" },
            additionalProperties = false
        }),
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                notificationId = new { type = "string" },
                deliveryKey = new { type = "string" },
                createdAt = new { type = "string" },
                recovered = new { type = "boolean" }
            },
            required = new[] { "notificationId", "deliveryKey", "createdAt", "recovered" },
            additionalProperties = false
        }));
}

public sealed class WorkNotificationMcpTool(
    WorkplaceService workplace,
    WorkNotificationMcpToolDefinitionProvider definitionProvider) : IInternalMcpToolHandler
{
    public InternalMcpToolDefinition Definition => definitionProvider.Definition;

    public async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Arguments.ValueKind != JsonValueKind.Object)
            throw new ToolDefinitionInvocationException("notification_arguments_invalid", "Notification arguments must be a JSON object.");
        var allowed = new HashSet<string>(["deliveryKey", "title", "message", "actionUrl"], StringComparer.Ordinal);
        var unknown = invocation.Arguments.EnumerateObject().Select(value => value.Name).FirstOrDefault(value => !allowed.Contains(value));
        if (unknown is not null)
            throw new ToolDefinitionInvocationException("notification_argument_unknown", $"Notification argument '{unknown}' is not declared by the Tool schema.");
        WorkplaceService.NotificationDelivery delivery;
        try
        {
            delivery = await workplace.DeliverNotificationAsync(new WorkplaceService.DeliverNotificationCommand(
                invocation.WorkspaceId,
                Required(invocation.Arguments, "deliveryKey"),
                Required(invocation.Arguments, "title"),
                Required(invocation.Arguments, "message"),
                Optional(invocation.Arguments, "actionUrl"),
                invocation.CorrelationId,
                invocation.RunId,
                invocation.FlowStepId,
                invocation.CallId), cancellationToken);
        }
        catch (WorkValidationException exception)
        {
            throw new ToolDefinitionInvocationException(exception.Code, exception.Message, exception);
        }
        return JsonSerializer.SerializeToElement(new
        {
            notificationId = delivery.Notification.Id.Value,
            deliveryKey = delivery.Notification.DeliveryKey,
            createdAt = delivery.Notification.CreatedAt,
            recovered = delivery.Recovered
        });
    }

    private static string Required(JsonElement arguments, string name) =>
        Optional(arguments, name) ?? throw new ToolDefinitionInvocationException("notification_argument_required", $"Notification argument '{name}' is required.");

    private static string? Optional(JsonElement arguments, string name) =>
        arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

public sealed class InternalMcpToolProjectionService(
    IControlPlaneStore store,
    IEnumerable<IInternalMcpToolDefinitionProvider> definitions,
    TimeProvider timeProvider)
{
    public async Task EnsureAsync(ResourceScopeRef workspaceScope, ResourceNamespace @namespace, CancellationToken cancellationToken)
    {
        if (workspaceScope.Kind != ResourceScopeKind.Workspace)
            throw new ToolResourceValidationException("Internal MCP Tools require a Workspace scope.");
        var providerKey = new ResourceKey(ResourceKinds.ToolProvider, AgentstrationToolProvider.Name, @namespace);
        if (await store.GetAsync<ToolProviderResource>(providerKey, cancellationToken) is { } provider)
        {
            if (provider.Value.Definition.Mcp?.Internal != true || provider.Value.ScopeRef != workspaceScope)
                throw new ToolResourceValidationException("The reserved Agentstration ToolProvider identity is already in use.");
        }
        else
            await store.PutAsync(Provider(workspaceScope, @namespace), null, true, cancellationToken);

        var now = timeProvider.GetUtcNow();
        foreach (var definition in definitions.Select(value => value.Definition))
        {
            var name = AgentstrationToolProvider.ToolResourceName(definition.Name);
            var key = new ResourceKey(ResourceKinds.Tool, name, @namespace);
            var existing = await store.GetAsync<ToolResource>(key, cancellationToken);
            if (existing is not null) continue;
            await store.PutAsync(new ToolResource
            {
                ApiVersion = ManagementApiVersions.CoreV1,
                Kind = ResourceKinds.Tool,
                Metadata = new ResourceMetadata { Name = name, Namespace = @namespace },
                ScopeRef = workspaceScope,
                Generation = 1,
                Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded },
                Definition = new ToolResourceProperties
                {
                    DisplayName = definition.DisplayName,
                    Description = definition.Description,
                    Enabled = true,
                    RequiresApproval = definition.RequiresApproval,
                    Provider = new ResourceReference(AgentstrationToolProvider.Name, workspaceScope, @namespace),
                    ExternalId = definition.Name,
                    Discovery = new ToolDiscoveryState { Available = true, FirstSeenAt = now, LastSeenAt = now },
                    Schema = new ToolSchema { Input = definition.InputSchema.Clone(), Output = definition.OutputSchema?.Clone() },
                    Metadata = new Dictionary<string, JsonElement>
                    {
                        ["agentstration.implementation"] = JsonSerializer.SerializeToElement("internal")
                    }
                }
            }, null, true, cancellationToken);
        }
    }

    private static ToolProviderResource Provider(ResourceScopeRef scope, ResourceNamespace @namespace) => new()
    {
        ApiVersion = ManagementApiVersions.CoreV1,
        Kind = ResourceKinds.ToolProvider,
        Metadata = new ResourceMetadata { Name = AgentstrationToolProvider.Name, Namespace = @namespace },
        ScopeRef = scope,
        Generation = 1,
        Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded },
        Definition = new ToolProviderProperties
        {
            DisplayName = "Agentstration",
            ProviderType = ToolProviderType.Mcp,
            Mcp = new McpToolProviderConfiguration { Internal = true },
            Discovery = new ToolProviderDiscoveryState
            {
                Status = "connected",
                Capabilities = new Dictionary<string, bool> { ["tools"] = true }
            }
        }
    };
}
