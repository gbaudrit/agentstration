using System.Text.Json;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Runtime.Core;

public sealed class ToolExecutionPipeline : IToolExecutionPipeline
{
    private readonly IToolInvoker invoker;
    private readonly IReadOnlyList<IToolExecutionHook> hooks;
    private readonly IReadOnlyList<IToolExecutionEventSink> eventSinks;
    private readonly TimeProvider timeProvider;

    public ToolExecutionPipeline(IToolInvoker invoker)
        : this(invoker, [], [], TimeProvider.System) { }

    public ToolExecutionPipeline(
        IToolInvoker invoker,
        IEnumerable<IToolExecutionEventSink> eventSinks,
        TimeProvider timeProvider)
        : this(invoker, [], eventSinks, timeProvider) { }

    public ToolExecutionPipeline(
        IToolInvoker invoker,
        IEnumerable<IToolExecutionHook> hooks,
        IEnumerable<IToolExecutionEventSink> eventSinks,
        TimeProvider timeProvider)
    {
        this.invoker = invoker;
        this.hooks = OrderHooks(hooks);
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
        DateTimeOffset? completionTimestamp = null;
        var enteredHooks = new List<IToolExecutionHook>(hooks.Count);
        var terminalHooksInvoked = false;
        try
        {
            foreach (var hook in hooks)
            {
                enteredHooks.Add(hook);
                ToolExecutionHookDecision decision;
                try
                {
                    decision = await hook.BeforeInvokeAsync(context, cancellationToken)
                        ?? throw new InvalidOperationException("A Tool execution hook returned no decision.");
                }
                catch (Exception exception) when (exception is not ToolExecutionDeniedException and not OperationCanceledException)
                {
                    throw new ToolExecutionHookException(hook.Id, "before-invoke", exception);
                }
                if (decision.Kind == ToolExecutionHookDecisionKind.Deny)
                    throw new ToolExecutionDeniedException(
                        hook.Id,
                        decision.Code ?? "tool_execution_denied",
                        decision.Message ?? $"Tool execution was denied by hook '{hook.Id}'.");
                if (decision.Kind != ToolExecutionHookDecisionKind.Allow)
                    throw new ToolExecutionHookException(
                        hook.Id,
                        "before-invoke",
                        new InvalidOperationException($"Unsupported Tool execution hook decision '{decision.Kind}'."));
            }
            result = await invoker.InvokeAsync(context, cancellationToken);
            terminalHooksInvoked = true;
            var succeededAt = timeProvider.GetUtcNow();
            completionTimestamp = succeededAt;
            await NotifyHooksAsync(
                enteredHooks,
                context,
                new ToolExecutionOutcome(ToolExecutionOutcomeKind.Succeeded, succeededAt, succeededAt - startedAt),
                cancellationToken);
        }
        catch (Exception exception)
        {
            var failedAt = timeProvider.GetUtcNow();
            var failure = Failure(exception);
            if (!terminalHooksInvoked)
            {
                try
                {
                    await NotifyHooksAsync(
                        enteredHooks,
                        context,
                        new ToolExecutionOutcome(
                            failure.Outcome,
                            failedAt,
                            failedAt - startedAt,
                            exception.GetType().FullName ?? exception.GetType().Name,
                            failure.Code,
                            exception.Message),
                        CancellationToken.None);
                }
                catch (Exception hookException)
                {
                    exception.Data["Agentstration.ToolExecutionTerminalHookError"] = hookException.Message;
                }
            }
            try
            {
                await PublishAsync(new ToolExecutionFailed(
                    context,
                    failedAt,
                    failedAt - startedAt,
                    exception.GetType().FullName ?? exception.GetType().Name,
                    exception.Message,
                    failure.Kind == ToolExecutionFailureKind.Cancelled)
                {
                    FailureKind = failure.Kind,
                    ErrorCode = failure.Code
                }, CancellationToken.None);
            }
            catch (Exception projectionException)
            {
                exception.Data["Agentstration.ToolExecutionProjectionError"] = projectionException.Message;
            }
            throw;
        }
        var completedAt = completionTimestamp ?? timeProvider.GetUtcNow();
        await PublishAsync(new ToolExecutionCompleted(context, completedAt, completedAt - startedAt), cancellationToken);
        return result;
    }

    private static IReadOnlyList<IToolExecutionHook> OrderHooks(IEnumerable<IToolExecutionHook> hooks)
    {
        ArgumentNullException.ThrowIfNull(hooks);
        var ordered = hooks.OrderBy(hook => hook.Order).ThenBy(hook => hook.Id, StringComparer.Ordinal).ToArray();
        foreach (var hook in ordered)
            ArgumentException.ThrowIfNullOrWhiteSpace(hook.Id);
        var duplicate = ordered.GroupBy(hook => hook.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Tool execution hook id '{duplicate.Key}' is registered more than once.");
        return ordered;
    }

    private static async ValueTask NotifyHooksAsync(
        IReadOnlyList<IToolExecutionHook> enteredHooks,
        ToolExecutionContext context,
        ToolExecutionOutcome outcome,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        for (var index = enteredHooks.Count - 1; index >= 0; index--)
        {
            var hook = enteredHooks[index];
            try
            {
                await hook.AfterInvokeAsync(context, outcome, cancellationToken);
            }
            catch (Exception exception)
            {
                failure ??= exception is OperationCanceledException && cancellationToken.IsCancellationRequested
                    ? exception
                    : new ToolExecutionHookException(hook.Id, "after-invoke", exception);
            }
        }
        if (failure is not null) throw failure;
    }

    private static (ToolExecutionFailureKind Kind, ToolExecutionOutcomeKind Outcome, string? Code) Failure(Exception exception) => exception switch
    {
        OperationCanceledException => (ToolExecutionFailureKind.Cancelled, ToolExecutionOutcomeKind.Cancelled, null),
        ToolExecutionDeniedException denied => (ToolExecutionFailureKind.Denied, ToolExecutionOutcomeKind.Denied, denied.Code),
        ToolExecutionHookException => (ToolExecutionFailureKind.Hook, ToolExecutionOutcomeKind.Failed, "tool_execution_hook_failed"),
        _ => (ToolExecutionFailureKind.Provider, ToolExecutionOutcomeKind.Failed, null)
    };

    private async ValueTask PublishAsync(ToolExecutionLifecycleEvent executionEvent, CancellationToken cancellationToken)
    {
        foreach (var sink in eventSinks)
            await sink.PublishAsync(executionEvent, cancellationToken);
    }
}
