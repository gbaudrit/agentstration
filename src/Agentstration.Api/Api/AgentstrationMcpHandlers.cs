using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Infrastructure.Notifications;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Tools.Mcp;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agentstration.Web.Api;

internal static class AgentstrationMcpHandlers
{
    public static async ValueTask<ListToolsResult> ListToolsAsync(
        RequestContext<ListToolsRequestParams> request,
        CancellationToken cancellationToken)
    {
        var services = request.Services ?? throw new InvalidOperationException("MCP request services are unavailable.");
        var current = services.GetRequiredService<ICurrentRequestContext>().Current;
        await services.GetRequiredService<InternalMcpToolProjectionService>().EnsureAsync(
            ResourceScopeRef.Workspace(current.WorkspaceId), ResourceNamespace.Default, cancellationToken);
        var definitions = services.GetRequiredService<ToolDefinitionService>();
        var values = await definitions.ListAsync(cancellationToken);
        var projected = (await services.GetRequiredService<IControlPlaneStore>()
                .ListAllAsync<ToolResource>(ResourceKinds.Tool, cancellationToken))
            .Select(value => value.Value)
            .Where(value => value.Namespace.IsDefault && value.Definition.Provider?.Name == AgentstrationToolProvider.Name)
            .ToDictionary(value => value.Definition.ExternalId ?? string.Empty, StringComparer.Ordinal);
        var builtIns = services.GetServices<IInternalMcpToolDefinitionProvider>()
            .Select(value => value.Definition)
            .Where(value => projected.TryGetValue(value.Name, out var tool)
                && tool.Definition.Enabled
                && tool.Definition.Discovery?.Available == true)
            .Select(value => new Tool
            {
                Name = value.Name,
                Title = value.DisplayName,
                Description = value.Description,
                InputSchema = value.InputSchema.Clone(),
                OutputSchema = value.OutputSchema?.Clone(),
                Meta = new JsonObject
                {
                    ["agentstration/namespace"] = ResourceNamespace.Default.Value,
                    ["agentstration/requiresApproval"] = value.RequiresApproval,
                    ["agentstration/implementation"] = "internal"
                }
            });
        return new ListToolsResult
        {
            Tools = builtIns.Concat(values
                .Select(value => value.Value)
                .Where(value => value.Definition.Enabled)
                .OrderBy(value => value.Namespace.Value, StringComparer.Ordinal)
                .ThenBy(value => value.Name, StringComparer.Ordinal)
                .Select(value => new Tool
                {
                    Name = PublicName(value),
                    Title = value.Definition.DisplayName,
                    Description = value.Definition.Description,
                    InputSchema = value.Definition.InputSchema.Clone(),
                    OutputSchema = value.Definition.OutputSchema?.Clone(),
                    Meta = new JsonObject
                    {
                        ["agentstration/namespace"] = value.Namespace.Value,
                        ["agentstration/requiresApproval"] = value.Definition.RequiresApproval,
                        ["agentstration/implementation"] = "flow"
                    }
                }))
                .OrderBy(value => value.Name, StringComparer.Ordinal)
                .ToList()
        };
    }

    public static async ValueTask<CallToolResult> CallToolAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken)
    {
        try
        {
            var parameters = request.Params ?? throw new ToolDefinitionInvocationException("tool_call_invalid", "Tool call parameters are required.");
            var services = request.Services ?? throw new ToolDefinitionInvocationException("tool_execution_scope_required", "MCP request services are unavailable.");
            var context = services.GetRequiredService<ICurrentRequestContext>();
            var current = context.Current;
            await services.GetRequiredService<InternalMcpToolProjectionService>().EnsureAsync(
                ResourceScopeRef.Workspace(current.WorkspaceId), ResourceNamespace.Default, cancellationToken);
            var arguments = JsonSerializer.SerializeToElement(parameters.Arguments ?? new Dictionary<string, JsonElement>());
            var builtIn = services.GetServices<IInternalMcpToolHandler>()
                .SingleOrDefault(value => string.Equals(value.Definition.Name, parameters.Name, StringComparison.Ordinal));
            if (builtIn is not null)
            {
                var callId = IdempotencyKey(parameters.Meta) ?? request.JsonRpcRequest.Id.ToString();
                var resourceName = AgentstrationToolProvider.ToolResourceName(builtIn.Definition.Name);
                var builtInOutput = await services.GetRequiredService<IToolExecutionPipeline>().ExecuteAsync(new ToolExecutionContext
                {
                    ToolCallId = callId,
                    InvocationId = $"{callId}:attempt:1",
                    ToolId = resourceName,
                    ToolNamespace = ResourceNamespace.Default,
                    ToolName = builtIn.Definition.Name,
                    ToolProviderId = AgentstrationToolProvider.Name,
                    ToolProviderNamespace = ResourceNamespace.Default,
                    ExternalToolId = builtIn.Definition.Name,
                    TenantId = current.TenantId,
                    WorkspaceId = new WorkspaceId(current.WorkspaceId),
                    PrincipalId = current.PrincipalId,
                    CorrelationId = Correlation(parameters.Meta),
                    Arguments = arguments
                }, cancellationToken);
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = builtInOutput?.GetRawText() ?? "null" }],
                    StructuredContent = builtInOutput
                };
            }
            var definitions = await services.GetRequiredService<ToolDefinitionService>().ListAsync(cancellationToken);
            var definition = definitions.Select(value => value.Value).SingleOrDefault(value =>
                value.Definition.Enabled && string.Equals(PublicName(value), parameters.Name, StringComparison.Ordinal))
                ?? throw new ToolDefinitionInvocationException("tool_definition_not_found", $"Tool '{parameters.Name}' is not published by the Agentstration MCP server.");
            if (definition.Definition.RequiresApproval)
                throw new ToolDefinitionInvocationException("tool_approval_required", $"Tool '{parameters.Name}' requires an approved Agent invocation.");
            var invocation = new ToolDefinitionInvocation(
                current.TenantId,
                new WorkspaceId(current.WorkspaceId),
                current.PrincipalId,
                definition.Namespace,
                definition.Name,
                IdempotencyKey(parameters.Meta) ?? request.JsonRpcRequest.Id.ToString(),
                Correlation(parameters.Meta),
                arguments,
                ToolDefinitionCallerKind.Mcp);
            var result = await services.GetRequiredService<IToolDefinitionExecutor>().ExecuteAsync(invocation, cancellationToken);
            var output = result.Output?.Clone();
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = output?.GetRawText() ?? "null" }],
                StructuredContent = output,
                Meta = JsonSerializer.SerializeToNode(result.Receipt)?.AsObject()
            };
        }
        catch (ToolDefinitionInvocationException exception)
        {
            return Error(exception.Code, exception.Message);
        }
        catch (ToolExecutionDeniedException exception)
        {
            return Error(exception.Code, exception.Message);
        }
        catch (ToolResolutionException exception)
        {
            return Error(exception.Code, exception.Message);
        }
    }

    private static CallToolResult Error(string code, string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
        Meta = new JsonObject { ["agentstration/errorCode"] = code }
    };

    private static string PublicName(ToolDefinitionResource definition) => definition.Namespace.IsDefault
        ? definition.Name
        : $"{definition.Namespace.Value}.{definition.Name}";

    private static string? Correlation(JsonObject? metadata) =>
        metadata?["agentstration/correlationId"]?.GetValue<string>();

    private static string? IdempotencyKey(JsonObject? metadata) =>
        metadata?["agentstration/idempotencyKey"]?.GetValue<string>();
}
