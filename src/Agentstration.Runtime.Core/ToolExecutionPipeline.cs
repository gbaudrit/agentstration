using System.Text.Json;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Runtime.Core;

public sealed class ToolExecutionPipeline(IToolInvoker invoker) : IToolExecutionPipeline
{
    public ValueTask<JsonElement?> ExecuteAsync(
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.ToolCallId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.InvocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.ToolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.ToolName);
        return invoker.InvokeAsync(context, cancellationToken);
    }
}
