using System.Text.Json;
using System.Text.Json.Nodes;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Resources;
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
        var definitions = services.GetRequiredService<ToolDefinitionService>();
        var values = await definitions.ListAsync(cancellationToken);
        return new ListToolsResult
        {
            Tools = values
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
                })
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
                JsonSerializer.SerializeToElement(parameters.Arguments ?? new Dictionary<string, JsonElement>()),
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
