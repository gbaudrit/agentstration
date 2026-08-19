using Agentstration.Runtime.Abstractions;

namespace Agentstration.Runtime.Core;

public sealed class RuntimeToolExecutionEventSink(RuntimeRunStateManager runs) : IToolExecutionEventSink
{
    public async ValueTask PublishAsync(
        ToolExecutionLifecycleEvent executionEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(executionEvent);
        var context = executionEvent.Context;
        if (context.OwnerKind != ToolExecutionOwnerKind.RuntimeRun)
            return;
        if (context.WorkspaceId is not { } workspaceId || string.IsNullOrWhiteSpace(context.RunId))
            throw new InvalidOperationException("A Runtime Run tool execution requires its Workspace and Run identities.");
        await runs.ProjectToolCallAsync(workspaceId, context.RunId, executionEvent, cancellationToken);
    }
}
