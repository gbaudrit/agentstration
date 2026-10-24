using System.Text.Json;
using Agentstration.Agents;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Flows.Storage.Abstractions;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Microsoft.Extensions.Options;

namespace Agentstration.Infrastructure.Runtime;

public sealed class RuntimeExecutionMaterialResolver(
    IRuntimeRunStore runtimeRuns,
    IFlowRepository flowRuns,
    IRuntimeRunExecutionScope runtimeScopes,
    IFlowRunExecutionScope flowScopes,
    IRuntimeAgentResolver agents,
    AgentManagementService agentManagement,
    IToolCatalog tools,
    IOptions<FlowRunExecutionOptions> flowOptions) : IRuntimeExecutionMaterialResolver
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
        var stored = await flowRuns.GetRunExactAsync(assignment.ExecutionScopeRef, flowRunId, cancellationToken)
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
        var stored = await runtimeRuns.GetExactAsync(assignment.ExecutionScopeRef, assignment.TargetRunId, cancellationToken)
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
            agent)
        {
            ScopeRef = stored.Value.ExecutionScopeRef
        };
    }

    public async Task<RuntimeRootFlowExecutionMaterial> ResolveFlowRunAsync(
        RuntimeWorkerAssignment assignment,
        string flowRunId,
        CancellationToken cancellationToken)
    {
        if (assignment.TargetKind != RuntimeAssignmentTargetKind.FlowRun)
            throw new RuntimeExecutionMaterialException("execution_coordinate_invalid", "Only a Flow assignment can resolve Flow material.");
        var stored = await flowRuns.GetRunExactAsync(assignment.ExecutionScopeRef, flowRunId, cancellationToken)
            ?? throw new RuntimeExecutionMaterialException("execution_run_not_found", "The assigned Flow Run was not found.");
        var run = stored.Value;
        if (!BelongsToAssignment(assignment, run))
            throw new RuntimeExecutionMaterialException("execution_coordinate_invalid", "The Flow Run does not belong to this root assignment.");
        await flowScopes.ValidateAsync(run.Scope, cancellationToken);
        using var scope = flowScopes.Enter(run.Scope);
        if (!string.Equals(run.FlowVersion, run.DefinitionSnapshot.Version, StringComparison.Ordinal)
            || run.DefinitionHash is not null
            && !string.Equals(run.DefinitionHash, run.DefinitionSnapshot.DefinitionHash, StringComparison.Ordinal)
            || run.DefinitionScopeRef is { } definitionScopeRef
            && definitionScopeRef != run.DefinitionSnapshot.ScopeRef)
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
                    new RuntimeAgentReference(binding.AgentResourceId, binding.AgentGeneration)
                    {
                        Namespace = binding.AgentNamespace,
                        ScopeRef = binding.AgentScopeRef,
                        ResourceUid = binding.AgentUid,
                        RevisionUid = binding.RevisionUid,
                        DefinitionHash = binding.DefinitionHash
                    },
                    cancellationToken);
            if (binding is not null && (!string.Equals(material.RevisionId, binding.RevisionId, StringComparison.Ordinal)
                || binding.AgentUid != Guid.Empty && material.AgentId != binding.AgentUid
                || binding.RevisionUid != Guid.Empty && material.RevisionUid != binding.RevisionUid
                || binding.DefinitionHash is not null && !string.Equals(material.DefinitionHash, binding.DefinitionHash, StringComparison.Ordinal)))
                throw new RuntimeExecutionMaterialException("execution_material_drift", $"Participant '{binding.ParticipantId}' no longer resolves to its pinned revision.");
            resolvedAgents.Add(material);
        }

        RuntimeFlowResumeMaterial? resume = null;
        if (run.RuntimeState is not null)
        {
            var answered = (await flowRuns.ListInputRequestsExactAsync(run.ExecutionScopeRef, run.Id,
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
            run.CorrelationId,
            flowOptions.Value.OrchestrationTimeout)
        {
            ScopeRef = run.ExecutionScopeRef,
            FlowScopeRef = (run.DefinitionScopeRef ?? run.DefinitionSnapshot.ScopeRef).Value
        };
    }

    private static bool BelongsToAssignment(RuntimeWorkerAssignment assignment, FlowRun run) =>
        string.Equals(run.Id, assignment.TargetRunId, StringComparison.Ordinal)
        || string.Equals(run.RootFlowRunId, assignment.TargetRunId, StringComparison.Ordinal);

    internal static IReadOnlyList<(string ParticipantId, FlowTargetReference Target)> ExecutionAgentReferences(FlowVersion version)
    {
        IEnumerable<(string ParticipantId, FlowTargetReference Target)> references = version.Graph is not null
            ? GraphAgentReferences(version.Graph)
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

    private static IEnumerable<(string ParticipantId, FlowTargetReference Target)> GraphAgentReferences(
        FlowGraphDefinition graph)
    {
        var staticAgents = graph.Steps.OfType<AgentFlowStepDefinition>()
            .Where(step => !step.Agent.ResourceId.StartsWith("${", StringComparison.Ordinal))
            .Select(step => (step.Name, new FlowTargetReference(FlowTargetKind.Agent,
                step.Agent.ResourceId,
                step.Agent.Version?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                step.Agent.Namespace,
                step.Agent.ScopeRef,
                step.Agent.ResourceUid,
                step.Agent.RevisionUid,
                step.Agent.DefinitionHash)));
        var routedAgents = graph.Steps.OfType<RouterFlowStepDefinition>()
            .SelectMany(router => router.Candidates.Select(candidate => candidate.Agent)
                .Append(router.Fallback).Where(target => target is not null).Cast<FlowResourceReference>())
            .Select(target => ($"Agent:{target.ResourceId}", new FlowTargetReference(
                FlowTargetKind.Agent, target.ResourceId,
                target.Version?.ToString(System.Globalization.CultureInfo.InvariantCulture), target.Namespace, target.ScopeRef,
                target.ResourceUid, target.RevisionUid, target.DefinitionHash)));
        return staticAgents.Concat(routedAgents)
            .GroupBy(value => new { value.Item2.Id, value.Item2.Version, value.Item2.Namespace, value.Item2.ScopeRef })
            .Select(group => group.First());
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
            reference.Namespace, reference.ResourceId, reference.ScopeRef, reference.Version, cancellationToken);
        var resolved = await agents.ResolveAsync(reference, cancellationToken);
        return await CreateAgentMaterialAsync(participantId, resolved, cancellationToken);
    }

    private async Task<RuntimeExecutionAgentMaterial> ResolveAgentAsync(
        string participantId,
        FlowTargetReference reference,
        CancellationToken cancellationToken)
    {
        var @namespace = reference.Namespace ?? ResourceNamespace.Default;
        var agent = await agentManagement.GetAgentAsync(@namespace, reference.Id, reference.ScopeRef, cancellationToken)
            ?? throw new RuntimeExecutionMaterialException("agent_not_found",
                $"Agent '{@namespace}/{reference.Id}' was not found.");
        var generation = agent.Value.Generation;
        if (!string.IsNullOrWhiteSpace(reference.Version)
            && (!long.TryParse(reference.Version, out generation) || generation < 1))
            throw new RuntimeExecutionMaterialException("agent_version_invalid",
                $"Agent version '{reference.Version}' is not a positive generation number.");
        var revision = await agentManagement.EnsureExecutionRevisionAsync(
            @namespace, agent.Value.Metadata.Name, agent.Value.ScopeRef, generation, cancellationToken);
        if (reference.ResourceUid is { } resourceUid && agent.Value.Uid != resourceUid
            || reference.RevisionUid is { } revisionUid && revision.Value.Uid != revisionUid
            || reference.DefinitionHash is { } definitionHash
                && !string.Equals(revision.Value.DefinitionHash, definitionHash, StringComparison.Ordinal))
            throw new RuntimeExecutionMaterialException("agent_reference_stale",
                $"Agent reference '{@namespace}/{reference.Id}' no longer matches its published identity.");
        var resolved = await agents.ResolveAsync(new RuntimeAgentReference(reference.Id, generation)
        {
            Namespace = @namespace,
            ScopeRef = agent.Value.ScopeRef,
            ResourceUid = reference.ResourceUid,
            RevisionUid = reference.RevisionUid,
            DefinitionHash = reference.DefinitionHash
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
        var resolvedTools = resolved.Definition.ToolReferences.Count > 0
            ? await tools.ResolveAsync(resolved.Definition.ToolReferences, cancellationToken)
            : await tools.ResolveAsync(resolved.Definition.EffectiveToolNames, cancellationToken);
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
            tool.ExternalId)
        {
            ScopeRef = tool.ScopeRef,
            Uid = tool.Uid,
            Generation = tool.Generation,
            ProviderScopeRef = tool.ProviderScopeRef,
            ProviderUid = tool.ProviderUid,
            ProviderGeneration = tool.ProviderGeneration
        }).ToArray();
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
            AgentScopeRef = resolved.AgentScopeRef,
            ModelProfileScopeRef = resolved.ModelProfileScopeRef,
            RevisionUid = resolved.RevisionUid,
            RuntimeProfileName = resolved.RuntimeProfileName,
            RuntimeProfileNamespace = resolved.RuntimeProfileNamespace
        };
    }
}
