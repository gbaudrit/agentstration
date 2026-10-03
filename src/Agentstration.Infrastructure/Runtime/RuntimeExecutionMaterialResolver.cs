using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Infrastructure.Runtime;

public sealed class RuntimeExecutionMaterialResolver(
    IRuntimeRunStore runtimeRuns,
    IFlowRepository flowRuns,
    IRuntimeRunExecutionScope runtimeScopes,
    IFlowRunExecutionScope flowScopes,
    IRuntimeAgentResolver agents,
    IToolCatalog tools) : IRuntimeExecutionMaterialResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<RuntimeExecutionMaterial> ResolveAsync(
        RuntimeWorkerAssignment assignment,
        CancellationToken cancellationToken) => assignment.TargetKind switch
    {
        RuntimeAssignmentTargetKind.RuntimeRun => await ResolveRuntimeRunAsync(assignment, cancellationToken),
        RuntimeAssignmentTargetKind.FlowRun => await ResolveFlowRunAsync(assignment, cancellationToken),
        _ => throw new RuntimeExecutionMaterialException("execution_target_unsupported", "The assignment target is unsupported.")
    };

    public async Task<RuntimeFlowStepMaterial> ResolveStepAsync(
        RuntimeWorkerAssignment assignment,
        string flowRunId,
        string flowVersion,
        string flowDefinitionHash,
        string stepDefinitionId,
        CancellationToken cancellationToken)
    {
        if (assignment.TargetKind != RuntimeAssignmentTargetKind.FlowRun
            || !string.Equals(assignment.TargetRunId, flowRunId, StringComparison.Ordinal))
            throw new RuntimeExecutionMaterialException("execution_coordinate_invalid", "The Flow Run does not belong to this assignment.");
        var stored = await flowRuns.GetRunAsync(assignment.WorkspaceId, flowRunId, cancellationToken)
            ?? throw new RuntimeExecutionMaterialException("execution_run_not_found", "The assigned Flow Run was not found.");
        var run = stored.Value;
        await flowScopes.ValidateAsync(run.Scope, cancellationToken);
        using var scope = flowScopes.Enter(run.Scope);
        if (!string.Equals(run.FlowVersion, flowVersion, StringComparison.Ordinal)
            || !string.Equals(run.DefinitionHash, flowDefinitionHash, StringComparison.Ordinal))
            throw new RuntimeExecutionMaterialException("execution_coordinate_invalid", "The step does not reference the pinned Flow version and hash.");
        var graph = run.DefinitionSnapshot.Graph
            ?? throw new RuntimeExecutionMaterialException("execution_coordinate_invalid", "The pinned Flow has no graph definition.");
        var position = -1;
        for (var index = 0; index < graph.Steps.Count; index++)
        {
            if (string.Equals(graph.Steps[index].Name, stepDefinitionId, StringComparison.Ordinal))
            {
                position = index;
                break;
            }
        }
        if (position < 0)
            throw new RuntimeExecutionMaterialException("execution_coordinate_invalid", "The StepDefinitionId is not present in the pinned Flow definition.");
        var step = graph.Steps[position];
        return new RuntimeFlowStepMaterial(run.Id, run.FlowVersion, run.DefinitionHash!, stepDefinitionId,
            step.DisplayName ?? step.Name, step.Type(), position);
    }

    private async Task<RuntimeExecutionMaterial> ResolveRuntimeRunAsync(
        RuntimeWorkerAssignment assignment,
        CancellationToken cancellationToken)
    {
        var stored = await runtimeRuns.GetAsync(assignment.WorkspaceId, assignment.TargetRunId, cancellationToken)
            ?? throw new RuntimeExecutionMaterialException("execution_run_not_found", "The assigned Runtime Run was not found.");
        await runtimeScopes.ValidateAsync(stored.Value.Scope, cancellationToken);
        using var scope = runtimeScopes.Enter(stored.Value.Scope);
        var agent = await ResolveAgentAsync("agent", stored.Value.Properties.Agent, cancellationToken);
        return new RuntimeDirectAgentExecutionMaterial(
            assignment.ExecutionMaterialId,
            assignment.ExecutionMaterialVersion,
            assignment.ExecutionMaterialDigest,
            assignment.WorkspaceId,
            stored.Value.Scope.TenantId,
            stored.Value.Scope.PrincipalId,
            assignment.TargetRunId,
            stored.Value.Properties.Input,
            stored.Value.Properties.Execution,
            agent);
    }

    private async Task<RuntimeExecutionMaterial> ResolveFlowRunAsync(
        RuntimeWorkerAssignment assignment,
        CancellationToken cancellationToken)
    {
        var stored = await flowRuns.GetRunAsync(assignment.WorkspaceId, assignment.TargetRunId, cancellationToken)
            ?? throw new RuntimeExecutionMaterialException("execution_run_not_found", "The assigned Flow Run was not found.");
        var run = stored.Value;
        await flowScopes.ValidateAsync(run.Scope, cancellationToken);
        using var scope = flowScopes.Enter(run.Scope);
        if (!string.Equals(run.FlowVersion, run.DefinitionSnapshot.Version, StringComparison.Ordinal)
            || !string.Equals(run.DefinitionHash, run.DefinitionSnapshot.DefinitionHash, StringComparison.Ordinal))
            throw new RuntimeExecutionMaterialException("execution_material_drift", "The Flow Run snapshot no longer matches its immutable version and hash pins.");

        var resolvedAgents = new List<RuntimeExecutionAgentMaterial>(run.RuntimeBindings.Count);
        foreach (var binding in run.RuntimeBindings)
        {
            var material = await ResolveAgentAsync(binding.ParticipantId,
                new RuntimeAgentReference(binding.AgentResourceId, binding.AgentGeneration) { Namespace = binding.AgentNamespace },
                cancellationToken);
            if (!string.Equals(material.RevisionId, binding.RevisionId, StringComparison.Ordinal))
                throw new RuntimeExecutionMaterialException("execution_material_drift", $"Participant '{binding.ParticipantId}' no longer resolves to its pinned revision.");
            resolvedAgents.Add(material);
        }

        return new RuntimeRootFlowExecutionMaterial(
            assignment.ExecutionMaterialId,
            assignment.ExecutionMaterialVersion,
            assignment.ExecutionMaterialDigest,
            assignment.WorkspaceId,
            run.Scope.TenantId,
            run.Scope.PrincipalId,
            run.Id,
            run.FlowId.Value,
            run.FlowId.Namespace.ToString(),
            run.FlowVersion,
            run.DefinitionHash ?? throw new RuntimeExecutionMaterialException("execution_material_invalid", "The Flow Run has no definition hash."),
            run.Input.Clone(),
            JsonSerializer.SerializeToElement(run.DefinitionSnapshot, JsonOptions),
            resolvedAgents);
    }

    private async Task<RuntimeExecutionAgentMaterial> ResolveAgentAsync(
        string participantId,
        RuntimeAgentReference reference,
        CancellationToken cancellationToken)
    {
        var resolved = await agents.ResolveAsync(reference, cancellationToken);
        if (!resolved.Ready)
            throw new RuntimeExecutionMaterialException("agent_not_ready", resolved.Error ?? $"Agent '{reference.ResourceId}' is not ready.");
        var resolvedTools = await tools.ResolveAsync(resolved.Definition.EffectiveToolNames, cancellationToken);
        var toolMaterials = resolvedTools.Select(tool => new RuntimeExecutionToolMaterial(
            tool.Id,
            tool.Namespace ?? default,
            tool.Name,
            tool.Description,
            tool.InputSchema.Clone(),
            tool.OutputSchema?.Clone(),
            tool.RequiresApproval,
            tool.ProviderId,
            tool.ProviderNamespace,
            tool.ExternalId)).ToArray();
        return new RuntimeExecutionAgentMaterial(
            $"agent:{resolved.AgentId:N}:{resolved.Generation}",
            participantId,
            resolved.AgentId,
            resolved.AgentName,
            resolved.Generation,
            resolved.RevisionId,
            resolved.Definition.DefinitionHash,
            resolved.Definition.Handler,
            resolved.Definition.DisplayName,
            resolved.Definition.Description,
            resolved.Definition.EffectiveInstructions,
            resolved.ModelProfileName,
            resolved.ModelProfileNamespace,
            toolMaterials);
    }
}
