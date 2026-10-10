using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Infrastructure.Runtime;

public sealed class RuntimeExecutionMaterialResolver(
    IRuntimeRunStore runtimeRuns,
    IFlowRepository flowRuns,
    IRuntimeRunExecutionScope runtimeScopes,
    IFlowRunExecutionScope flowScopes,
    IRuntimeAgentResolver agents,
    AgentManagementService agentManagement,
    IToolCatalog tools) : IRuntimeExecutionMaterialResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<RuntimeExecutionMaterial> ResolveAsync(
        RuntimeWorkerAssignment assignment,
        CancellationToken cancellationToken) => assignment.TargetKind switch
        {
            RuntimeAssignmentTargetKind.RuntimeRun => await ResolveRuntimeRunAsync(assignment, cancellationToken),
            RuntimeAssignmentTargetKind.FlowRun => await ResolveFlowRunAsync(assignment, assignment.TargetRunId, cancellationToken),
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
        if (assignment.TargetKind != RuntimeAssignmentTargetKind.FlowRun)
            throw new RuntimeExecutionMaterialException("execution_coordinate_invalid", "The Flow Run does not belong to this assignment.");
        var stored = await flowRuns.GetRunAsync(assignment.WorkspaceId, flowRunId, cancellationToken)
            ?? throw new RuntimeExecutionMaterialException("execution_run_not_found", "The assigned Flow Run was not found.");
        var run = stored.Value;
        if (!BelongsToAssignment(assignment, run))
            throw new RuntimeExecutionMaterialException("execution_coordinate_invalid", "The Flow Run does not belong to this root assignment.");
        await flowScopes.ValidateAsync(run.Scope, cancellationToken);
        using var scope = flowScopes.Enter(run.Scope);
        if (!string.Equals(run.FlowVersion, flowVersion, StringComparison.Ordinal)
            || !string.Equals(DefinitionHash(run), flowDefinitionHash, StringComparison.Ordinal))
            throw new RuntimeExecutionMaterialException("execution_coordinate_invalid", "The step does not reference the pinned Flow version and hash.");
        var steps = ExecutionSteps(run.DefinitionSnapshot);
        var position = -1;
        for (var index = 0; index < steps.Count; index++)
        {
            if (string.Equals(steps[index].Name, stepDefinitionId, StringComparison.Ordinal))
            {
                position = index;
                break;
            }
        }
        if (position < 0)
            throw new RuntimeExecutionMaterialException("execution_coordinate_invalid", "The StepDefinitionId is not present in the pinned Flow definition.");
        var step = steps[position];
        return new RuntimeFlowStepMaterial(run.Id, run.FlowVersion, DefinitionHash(run), stepDefinitionId,
            step.DisplayName, step.Type, position);
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

    public async Task<RuntimeRootFlowExecutionMaterial> ResolveFlowRunAsync(
        RuntimeWorkerAssignment assignment,
        string flowRunId,
        CancellationToken cancellationToken)
    {
        if (assignment.TargetKind != RuntimeAssignmentTargetKind.FlowRun)
            throw new RuntimeExecutionMaterialException("execution_coordinate_invalid", "Only a Flow assignment can resolve Flow material.");
        var stored = await flowRuns.GetRunAsync(assignment.WorkspaceId, flowRunId, cancellationToken)
            ?? throw new RuntimeExecutionMaterialException("execution_run_not_found", "The assigned Flow Run was not found.");
        var run = stored.Value;
        if (!BelongsToAssignment(assignment, run))
            throw new RuntimeExecutionMaterialException("execution_coordinate_invalid", "The Flow Run does not belong to this root assignment.");
        await flowScopes.ValidateAsync(run.Scope, cancellationToken);
        using var scope = flowScopes.Enter(run.Scope);
        if (!string.Equals(run.FlowVersion, run.DefinitionSnapshot.Version, StringComparison.Ordinal)
            || run.DefinitionHash is not null
            && !string.Equals(run.DefinitionHash, run.DefinitionSnapshot.DefinitionHash, StringComparison.Ordinal))
            throw new RuntimeExecutionMaterialException("execution_material_drift", "The Flow Run snapshot no longer matches its immutable version and hash pins.");

        var references = ExecutionAgentReferences(run.DefinitionSnapshot);
        var resolvedAgents = new List<RuntimeExecutionAgentMaterial>(references.Count);
        foreach (var reference in references)
        {
            var binding = run.RuntimeBindings.SingleOrDefault(value =>
                string.Equals(value.ParticipantId, reference.ParticipantId, StringComparison.Ordinal));
            var material = binding is null
                ? await ResolveAgentAsync(reference.ParticipantId, reference.Target, cancellationToken)
                : await ResolveAgentAsync(reference.ParticipantId,
                    new RuntimeAgentReference(binding.AgentResourceId, binding.AgentGeneration) { Namespace = binding.AgentNamespace },
                    cancellationToken);
            if (binding is not null && !string.Equals(material.RevisionId, binding.RevisionId, StringComparison.Ordinal))
                throw new RuntimeExecutionMaterialException("execution_material_drift", $"Participant '{binding.ParticipantId}' no longer resolves to its pinned revision.");
            resolvedAgents.Add(material);
        }

        RuntimeFlowResumeMaterial? resume = null;
        if (run.RuntimeState is not null)
        {
            var answered = (await flowRuns.ListInputRequestsAsync(run.WorkspaceId, run.Id,
                InputRequestStatus.Answered, cancellationToken)).LastOrDefault();
            if (answered?.Value.Response is { } response)
                resume = new(run.RuntimeState.RuntimeType, run.RuntimeState.StateId,
                    answered.Value.Id, answered.Value.RuntimeRequestId, answered.Value.Prompt,
                    answered.Value.Type.ToString(), answered.Value.Options, answered.Value.Source,
                    response.Value.Clone(), response.ReceivedAt, response.PrincipalId);
        }

        return new RuntimeRootFlowExecutionMaterial(
            string.Equals(run.Id, assignment.TargetRunId, StringComparison.Ordinal)
                ? assignment.ExecutionMaterialId : $"{assignment.ExecutionMaterialId}:child:{run.Id}",
            assignment.ExecutionMaterialVersion,
            assignment.ExecutionMaterialDigest,
            assignment.WorkspaceId,
            run.Scope.TenantId,
            run.Scope.PrincipalId,
            run.Id,
            run.FlowId.Value,
            run.FlowId.Namespace.ToString(),
            run.FlowVersion,
            DefinitionHash(run),
            run.Input.Clone(),
            JsonSerializer.SerializeToElement(run.DefinitionSnapshot, JsonOptions),
            resolvedAgents,
            resume,
            run.RootFlowRunId ?? run.Id,
            run.ParentFlowRunId,
            run.CorrelationId);
    }

    private static bool BelongsToAssignment(RuntimeWorkerAssignment assignment, FlowRun run) =>
        string.Equals(run.Id, assignment.TargetRunId, StringComparison.Ordinal)
        || string.Equals(run.RootFlowRunId, assignment.TargetRunId, StringComparison.Ordinal);

    private static IReadOnlyList<(string ParticipantId, FlowTargetReference Target)> ExecutionAgentReferences(FlowVersion version)
    {
        IEnumerable<(string ParticipantId, FlowTargetReference Target)> references = version.Graph is not null
            ? version.Graph.Steps.OfType<AgentFlowStepDefinition>().Select(step => (step.Name,
                new FlowTargetReference(FlowTargetKind.Agent, step.Agent.ResourceId,
                    step.Agent.Version?.ToString(System.Globalization.CultureInfo.InvariantCulture), step.Agent.Namespace)))
            : version.Definition switch
            {
                DirectFlowDefinition direct => [("Agent", direct.Target)],
                RoutingFlowDefinition routing => routing.Destinations
                    .Append(routing.Fallback)
                    .Where(value => value is not null)
                    .Select((value, index) => ($"Agent:{index}", value!)),
                OrchestrationFlowDefinition orchestration => orchestration.Participants
                    .Concat(orchestration.Pattern is MagenticOrchestrationPattern magentic ? [magentic.Manager] : [])
                    .Select(value => (value.Id, value)),
                _ => []
            };
        return references
            .Where(value => value.Target.Kind == FlowTargetKind.Agent)
            .GroupBy(value => value.ParticipantId, StringComparer.Ordinal)
            .Select(group => group.First())
            .Select(value => (value.ParticipantId, value.Target with
            {
                Namespace = value.Target.Namespace ?? version.FlowId.Namespace
            }))
            .ToArray();
    }

    private static IReadOnlyList<(string Name, string DisplayName, string Type)> ExecutionSteps(FlowVersion version)
    {
        if (version.Graph is not null)
            return version.Graph.Steps.Select(step => (step.Name, step.DisplayName ?? step.Name, step.Type())).ToArray();
        var steps = new List<(string, string, string)> { ("Input", "Input", "input") };
        if (version.Definition is RoutingFlowDefinition) steps.Add(("Router", "Router", "router"));
        if (version.Definition is OrchestrationFlowDefinition orchestration)
        {
            steps.AddRange(orchestration.Participants.Select(value => (value.Id, value.Id, "agent")));
            if (orchestration.Pattern is MagenticOrchestrationPattern magentic
                && steps.All(value => !string.Equals(value.Item1, magentic.Manager.Id, StringComparison.Ordinal)))
                steps.Add((magentic.Manager.Id, magentic.Manager.Id, "agent"));
        }
        else
            steps.Add(("Agent", "Agent", "agent"));
        steps.Add(("Output", "Output", "output"));
        return steps;
    }

    private static string DefinitionHash(FlowRun run) => run.DefinitionHash
        ?? $"sha256:{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(run.DefinitionSnapshot, JsonOptions))))}";

    private async Task<RuntimeExecutionAgentMaterial> ResolveAgentAsync(
        string participantId,
        RuntimeAgentReference reference,
        CancellationToken cancellationToken)
    {
        _ = await agentManagement.EnsureExecutionRevisionAsync(
            reference.Namespace, reference.ResourceId, reference.Version, cancellationToken);
        var resolved = await agents.ResolveAsync(reference, cancellationToken);
        return await CreateAgentMaterialAsync(participantId, resolved, cancellationToken);
    }

    private async Task<RuntimeExecutionAgentMaterial> ResolveAgentAsync(
        string participantId,
        FlowTargetReference reference,
        CancellationToken cancellationToken)
    {
        var @namespace = reference.Namespace ?? ResourceNamespace.Default;
        var agent = await agentManagement.GetAgentAsync(@namespace, reference.Id, cancellationToken)
            ?? throw new RuntimeExecutionMaterialException("agent_not_found",
                $"Agent '{@namespace}/{reference.Id}' was not found.");
        var generation = agent.Value.Generation;
        if (!string.IsNullOrWhiteSpace(reference.Version)
            && (!long.TryParse(reference.Version, out generation) || generation < 1))
            throw new RuntimeExecutionMaterialException("agent_version_invalid",
                $"Agent version '{reference.Version}' is not a positive generation number.");
        _ = await agentManagement.EnsureExecutionRevisionAsync(
            @namespace, agent.Value.Metadata.Name, generation, cancellationToken);
        var resolved = await agents.ResolveAsync(new RuntimeAgentReference(reference.Id, generation)
        {
            Namespace = @namespace
        }, cancellationToken);
        return await CreateAgentMaterialAsync(participantId, resolved, cancellationToken);
    }

    private async Task<RuntimeExecutionAgentMaterial> CreateAgentMaterialAsync(
        string participantId,
        ResolvedRuntimeAgent resolved,
        CancellationToken cancellationToken)
    {
        if (!resolved.Ready)
            throw new RuntimeExecutionMaterialException("agent_not_ready", resolved.Error ?? $"Agent '{resolved.AgentName}' is not ready.");
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
            toolMaterials)
        {
            AgentNamespace = resolved.AgentNamespace,
            RuntimeProfileName = resolved.RuntimeProfileName,
            RuntimeProfileNamespace = resolved.RuntimeProfileNamespace
        };
    }
}
