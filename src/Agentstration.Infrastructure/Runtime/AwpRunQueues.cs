using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;

namespace Agentstration.Infrastructure.Runtime;

public sealed class AwpRuntimeRunQueue(
    IRuntimeRunStore runs,
    RuntimeWorkerAssignmentService assignments) : IRuntimeRunQueue
{
    private const string MicrosoftAgentFramework = "microsoft-agent-framework";
    public async ValueTask EnqueueAsync(RuntimeRunQueueItem item, CancellationToken cancellationToken)
    {
        var run = await runs.GetExactAsync(item.Scope.ScopeRef, item.RunId, cancellationToken)
            ?? throw new RuntimeRunNotFoundException(item.RunId);
        if (run.Value.Scope != item.Scope || run.Value.Status.State.IsTerminal()) return;
        if (await assignments.GetByTargetExactAsync(item.Scope.ScopeRef, RuntimeAssignmentTargetKind.RuntimeRun,
                item.RunId, cancellationToken) is not null) return;

        await assignments.CreateExactAsync(item.Scope.ScopeRef,
            item.Scope.TenantId == Guid.Empty ? null : item.Scope.TenantId,
            RuntimeAssignmentTargetKind.RuntimeRun, item.RunId,
            MicrosoftAgentFramework, "1.0", RuntimeExecutionMaterialVersions.V1,
            $"runtime-run:{item.RunId}", Digest($"{item.Scope.ScopeRef}:{item.RunId}:{run.Value.Properties.Agent.Namespace}:{run.Value.Properties.Agent.ResourceId}:{run.Value.Properties.Agent.Version}"),
            cancellationToken);
    }

    public async IAsyncEnumerable<RuntimeRunQueueItem> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }

    public async ValueTask RequestCancellationAsync(RuntimeRunQueueItem item, CancellationToken cancellationToken)
    {
        var assignment = await assignments.GetByTargetExactAsync(item.Scope.ScopeRef,
            RuntimeAssignmentTargetKind.RuntimeRun, item.RunId, cancellationToken);
        if (assignment is not null)
            await assignments.RequestCancellationExactAsync(item.Scope.ScopeRef, assignment.Value.Id, cancellationToken);
    }

    private static string Digest(string value) => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}";
}

public sealed class AwpFlowRunQueue(
    IFlowRepository runs,
    RuntimeWorkerAssignmentService assignments) : IFlowRunQueue
{
    private const string MicrosoftAgentFramework = "microsoft-agent-framework";
    public async ValueTask EnqueueAsync(FlowRunQueueItem item, CancellationToken cancellationToken)
    {
        var run = await runs.GetRunExactAsync(item.Scope.ScopeRef, item.RunId, cancellationToken)
            ?? throw new FlowRunNotFoundException(item.RunId);
        if (run.Value.Scope != item.Scope || run.Value.Status.IsTerminal() || run.Value.RootFlowRunId is not null) return;
        var existing = await assignments.GetByTargetExactAsync(item.Scope.ScopeRef, RuntimeAssignmentTargetKind.FlowRun,
            item.RunId, cancellationToken);
        if (existing is not null)
        {
            if (run.Value.Status == FlowRunStatus.WaitingForInput
                && existing.Value.State == RuntimeAssignmentState.Succeeded)
                await assignments.RequeueExactAsync(item.Scope.ScopeRef, existing.Value.Id, cancellationToken);
            return;
        }

        var hash = run.Value.DefinitionHash ?? Digest(JsonSerializer.Serialize(run.Value.DefinitionSnapshot));
        await assignments.CreateExactAsync(item.Scope.ScopeRef,
            item.Scope.TenantId == Guid.Empty ? null : item.Scope.TenantId,
            RuntimeAssignmentTargetKind.FlowRun, item.RunId,
            MicrosoftAgentFramework, "1.0", RuntimeExecutionMaterialVersions.V1,
            $"flow-run:{item.RunId}", Digest($"{item.Scope.ScopeRef}:{item.RunId}:{run.Value.FlowId}:{run.Value.FlowVersion}:{hash}"),
            cancellationToken);
    }

    public async ValueTask RequestCancellationAsync(FlowRunQueueItem item, CancellationToken cancellationToken)
    {
        var run = await runs.GetRunExactAsync(item.Scope.ScopeRef, item.RunId, cancellationToken);
        if (run is null) return;
        var rootRunId = run.Value.RootFlowRunId ?? run.Value.Id;
        var assignment = await assignments.GetByTargetExactAsync(item.Scope.ScopeRef,
            RuntimeAssignmentTargetKind.FlowRun, rootRunId, cancellationToken);
        if (assignment is not null)
            await assignments.RequestCancellationExactAsync(item.Scope.ScopeRef, assignment.Value.Id, cancellationToken);
    }

    private static string Digest(string value) => $"sha256:{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))}";
}
