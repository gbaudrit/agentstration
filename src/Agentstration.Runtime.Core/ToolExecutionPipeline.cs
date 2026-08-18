using System.Text.Json;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Runtime.Core;

public sealed class ToolExecutionPipeline : IToolExecutionPipeline
{
    private readonly IToolInvoker invoker;
    private readonly IReadOnlyList<IToolExecutionEventSink> eventSinks;
    private readonly TimeProvider timeProvider;

    public ToolExecutionPipeline(IToolInvoker invoker)
        : this(invoker, [], TimeProvider.System) { }

    public ToolExecutionPipeline(
        IToolInvoker invoker,
        IEnumerable<IToolExecutionEventSink> eventSinks,
        TimeProvider timeProvider)
    {
        this.invoker = invoker;
        this.eventSinks = eventSinks.ToArray();
        this.timeProvider = timeProvider;
    }

    public async ValueTask<JsonElement?> ExecuteAsync(
        ToolExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.ToolCallId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.InvocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.ToolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.ToolName);
        var startedAt = timeProvider.GetUtcNow();
        await PublishAsync(new ToolExecutionStarted(context, startedAt), cancellationToken);
        JsonElement? result;
        try
        {
            result = await invoker.InvokeAsync(context, cancellationToken);
        }
        catch (Exception exception)
        {
            var failedAt = timeProvider.GetUtcNow();
            try
            {
                await PublishAsync(new ToolExecutionFailed(
                    context,
                    failedAt,
                    failedAt - startedAt,
                    exception.GetType().FullName ?? exception.GetType().Name,
                    exception.Message,
                    exception is OperationCanceledException), CancellationToken.None);
            }
            catch (Exception projectionException)
            {
                exception.Data["Agentstration.ToolExecutionProjectionError"] = projectionException.Message;
            }
            throw;
        }
        var completedAt = timeProvider.GetUtcNow();
        await PublishAsync(new ToolExecutionCompleted(context, completedAt, completedAt - startedAt), cancellationToken);
        return result;
    }

    private async ValueTask PublishAsync(ToolExecutionLifecycleEvent executionEvent, CancellationToken cancellationToken)
    {
        foreach (var sink in eventSinks)
            await sink.PublishAsync(executionEvent, cancellationToken);
    }
}
