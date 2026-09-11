using Agentstration.Application.Work;
using Agentstration.Flows;
using Agentstration.Flows.Application;

namespace Agentstration.Infrastructure.Flows;

public sealed class RootFlowTargetResolver(FlowService flows) : IRootFlowTargetResolver
{
    public async Task<ResolvedRootFlowTarget> ResolveAsync(
        FlowRunScope scope,
        FlowReference target,
        System.Text.Json.JsonElement input,
        CancellationToken cancellationToken)
    {
        var resolved = await flows.ResolveAsync(scope.WorkspaceId, target, target.FlowId.Namespace, cancellationToken);
        FlowRunService.ValidateInput(resolved.Graph?.InputSchema, input);
        return new(new FlowReference(resolved.FlowId, resolved.Version, false, resolved.FlowId.Namespace));
    }
}

public sealed class RootFlowRunGateway(FlowRunService runs) : IRootFlowRunGateway
{
    public async Task<RootFlowRunResult> EnsureAsync(RootFlowRunRequest request, CancellationToken cancellationToken)
    {
        var stored = await runs.EnsureRootAsync(new EnsureRootFlowRunCommand(
            request.RunId,
            request.Target.FlowId,
            request.Target.UseActiveVersion ? null : request.Target.Version,
            "local",
            request.Trigger,
            request.Origin,
            request.CallerId,
            request.CausationId,
            request.IdempotencyKey,
            request.CorrelationId,
            request.Input,
            request.WorkItemId.Value.ToString("D"),
            request.ResolvedFromActiveReference,
            request.ParentFlowRunId,
            request.InteractionId,
            request.WorkTaskId,
            request.TriggerMessageId,
            request.Scope), cancellationToken);
        return new(stored.Value, stored.ETag);
    }
}

public sealed class RootFlowSubmissionAuthorizer(IFlowRunExecutionScope executionScope) : IRootFlowSubmissionAuthorizer
{
    public async Task AuthorizeAsync(FlowRunScope scope, CancellationToken cancellationToken) =>
        await executionScope.ValidateAsync(scope, cancellationToken);
}
