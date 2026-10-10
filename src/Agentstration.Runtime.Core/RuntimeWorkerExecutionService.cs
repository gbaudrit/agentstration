using System.Text.Json;
using Agentstration.Runtime.Abstractions;

namespace Agentstration.Runtime.Core;

public sealed class RuntimeWorkerExecutionService(
    RuntimeWorkerAssignmentService assignments,
    IRuntimeExecutionMaterialResolver materials,
    IRuntimeWorkerOperationGateway operations,
    RuntimeWorkerLeaseOptions leaseOptions,
    RuntimeAssignmentLeaseGuardRegistry leaseGuards)
{
    public async Task<RuntimeExecutionMaterial> GetMaterialAsync(
        RuntimeAssignmentOwnershipProof proof,
        CancellationToken cancellationToken)
    {
        var authorization = await assignments.AuthorizeAsync(proof, cancellationToken);
        var material = await materials.ResolveAsync(authorization.Assignment, cancellationToken);
        if (material.WorkspaceId != proof.WorkspaceId
            || authorization.Assignment.TenantId != Guid.Empty && material.TenantId != authorization.Assignment.TenantId
            || material.TargetKind != authorization.Assignment.TargetKind
            || !string.Equals(material.RunId, authorization.Assignment.TargetRunId, StringComparison.Ordinal)
            || !string.Equals(material.MaterialId, authorization.Assignment.ExecutionMaterialId, StringComparison.Ordinal)
            || !string.Equals(material.SchemaVersion, authorization.Assignment.ExecutionMaterialVersion, StringComparison.Ordinal)
            || !string.Equals(material.Digest, authorization.Assignment.ExecutionMaterialDigest, StringComparison.Ordinal))
            throw new RuntimeExecutionMaterialException("execution_material_drift", "Resolved execution material does not match the assigned immutable reference.");
        return material;
    }

    public async Task<RuntimeAssignmentStepExecution> OpenStepExecutionAsync(
        RuntimeAssignmentOwnershipProof proof,
        Guid commandId,
        string flowRunId,
        string flowVersion,
        string flowDefinitionHash,
        string stepDefinitionId,
        CancellationToken cancellationToken)
    {
        var authorization = await assignments.AuthorizeAsync(proof, cancellationToken);
        var step = await materials.ResolveStepAsync(authorization.Assignment, flowRunId, flowVersion,
            flowDefinitionHash, stepDefinitionId, cancellationToken);
        return await assignments.OpenStepExecutionAsync(proof, commandId, step.FlowRunId, step.FlowVersion,
            step.FlowDefinitionHash, step.StepDefinitionId, step.StepName, step.StepType,
            step.DefinitionPosition, cancellationToken);
    }

    public async Task<RuntimeAssignmentTurn> OpenTurnAsync(
        RuntimeAssignmentOwnershipProof proof,
        Guid commandId,
        string runId,
        Guid? stepExecutionId,
        string? participantId,
        CancellationToken cancellationToken)
    {
        var authorization = await assignments.AuthorizeAsync(proof, cancellationToken);
        IReadOnlyList<string> participants;
        if (authorization.Assignment.TargetKind == RuntimeAssignmentTargetKind.RuntimeRun)
            participants = ["agent"];
        else if (stepExecutionId is { } flowStepId)
        {
            var flowMaterial = await materials.ResolveFlowRunAsync(authorization.Assignment,
                authorization.Assignment.StepExecutions.Single(value => value.Id == flowStepId).FlowRunId,
                cancellationToken);
            participants = flowMaterial.Agents.Select(value => value.ParticipantId).ToArray();
        }
        else
        {
            participants = (await GetMaterialAsync(proof, cancellationToken) as RuntimeRootFlowExecutionMaterial)?.Agents
                .Select(value => value.ParticipantId).ToArray() ?? [];
        }
        if (participantId is not null && !participants.Contains(participantId, StringComparer.Ordinal))
            throw new RuntimeExecutionMaterialException("execution_coordinate_invalid", "The participant is not present in the assigned execution material.");
        if (authorization.Assignment.TargetKind == RuntimeAssignmentTargetKind.FlowRun && stepExecutionId is null)
            throw new RuntimeExecutionMaterialException("execution_coordinate_invalid", "A Flow Turn must belong to a server-authorized StepExecution.");
        return await assignments.OpenTurnAsync(proof, commandId, runId, stepExecutionId, participantId, cancellationToken);
    }

    public Task<RuntimeAssignmentEventAppendResult> AppendEventsAsync(
        RuntimeAssignmentOwnershipProof proof,
        IReadOnlyList<RuntimeAssignmentExecutionEvent> events,
        CancellationToken cancellationToken) => assignments.AppendEventsAsync(proof, events, cancellationToken);

    public async Task<RuntimeAssignmentCheckpoint> StoreCheckpointAsync(
        RuntimeAssignmentOwnershipProof proof,
        string checkpointId,
        string schemaVersion,
        string compatibilityKey,
        JsonElement payload,
        CancellationToken cancellationToken)
    {
        var authorization = await assignments.AuthorizeAsync(proof, cancellationToken);
        if (!string.Equals(compatibilityKey, authorization.Assignment.ExecutionMaterialDigest, StringComparison.Ordinal))
            throw new RuntimeExecutionMaterialException("checkpoint_incompatible", "The checkpoint compatibility key does not match the assigned execution material.");
        await assignments.StoreCheckpointAsync(proof, checkpointId, schemaVersion, compatibilityKey, payload, cancellationToken);
        return await assignments.GetCheckpointAsync(proof, checkpointId, cancellationToken)
            ?? throw new InvalidOperationException("The persisted checkpoint could not be read back.");
    }

    public Task<RuntimeAssignmentCheckpoint?> GetCheckpointAsync(
        RuntimeAssignmentOwnershipProof proof,
        string checkpointId,
        CancellationToken cancellationToken) => assignments.GetCheckpointAsync(proof, checkpointId, cancellationToken);

    public async Task<RuntimeGovernedModelResponse> InvokeModelAsync(
        RuntimeAssignmentOwnershipProof proof,
        string participantId,
        Guid turnId,
        Guid turnAttemptId,
        IReadOnlyList<RuntimeGovernedModelMessage> messages,
        CancellationToken cancellationToken)
    {
        if (messages.Count is < 1 or > 256 || ModelPayloadLength(messages) > 1_048_576)
            throw new RuntimeExecutionMaterialException("model_request_too_large",
                "A model request must contain between 1 and 256 messages and cannot exceed 1 MiB.");
        var (authorization, material, agent) = await AuthorizeParticipantSideEffectAsync(proof, participantId, turnId, cancellationToken);
        _ = authorization.Assignment.Turns.SingleOrDefault(value => value.Id == turnId
            && value.AttemptId == turnAttemptId && string.Equals(value.ParticipantId, participantId, StringComparison.Ordinal))
            ?? throw new RuntimeExecutionMaterialException("execution_coordinate_invalid", "The model call does not reference an authorized Turn and TurnAttempt.");
        using var lease = await CreateLeaseGuardAsync(proof, authorization, cancellationToken);
        var options = material is RuntimeDirectAgentExecutionMaterial direct
            ? direct.Execution
            : new RuntimeExecutionOptions();
        return await operations.InvokeModelAsync(new RuntimeGovernedModelRequest(
            agent, messages, options, material.TenantId, material.WorkspaceId, material.PrincipalId), lease.Token);
    }

    public async Task<JsonElement?> InvokeToolAsync(
        RuntimeAssignmentOwnershipProof proof,
        string participantId,
        Guid turnId,
        Guid turnAttemptId,
        Guid toolCallId,
        string toolId,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        if (arguments?.GetRawText().Length > 1_048_576)
            throw new RuntimeExecutionMaterialException("tool_arguments_too_large", "Tool arguments cannot exceed 1 MiB.");
        var (authorization, material, agent) = await AuthorizeParticipantSideEffectAsync(proof, participantId, turnId, cancellationToken);
        var turn = authorization.Assignment.Turns.SingleOrDefault(value => value.Id == turnId
            && value.AttemptId == turnAttemptId && string.Equals(value.ParticipantId, participantId, StringComparison.Ordinal))
            ?? throw new RuntimeExecutionMaterialException("execution_coordinate_invalid", "The Tool call does not reference an authorized Turn and TurnAttempt.");
        var tool = agent.Tools.SingleOrDefault(value => string.Equals(value.Id, toolId, StringComparison.Ordinal))
            ?? throw new RuntimeExecutionMaterialException("tool_not_authorized", "The Tool is not declared by the assigned Agent revision.");
        using var lease = await CreateLeaseGuardAsync(proof, authorization, cancellationToken);
        return await operations.InvokeToolAsync(new RuntimeGovernedToolRequest(
            agent, tool, toolCallId, turnId, turnAttemptId, turn.StepExecutionId,
            material.TenantId, material.WorkspaceId, material.PrincipalId, material.RunId,
            arguments, material is RuntimeDirectAgentExecutionMaterial direct ? direct.Execution.PersistToolArguments : null), lease.Token);
    }

    public async Task<RuntimeGovernedArtifact> StoreArtifactAsync(
        RuntimeAssignmentOwnershipProof proof,
        string name,
        string contentType,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeSideEffectAsync(proof, cancellationToken);
        using var lease = await CreateLeaseGuardAsync(proof, authorization, cancellationToken);
        return await operations.StoreArtifactAsync(proof.WorkspaceId, proof.AssignmentId, name, contentType, content, lease.Token);
    }

    public async Task<RuntimeGovernedArtifact?> GetArtifactAsync(
        RuntimeAssignmentOwnershipProof proof,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        _ = await assignments.AuthorizeAsync(proof, cancellationToken);
        return await operations.GetArtifactAsync(proof.WorkspaceId, proof.AssignmentId, artifactId, cancellationToken);
    }

    public async Task<RuntimeGovernedChildFlow> CreateOrGetChildFlowAsync(
        RuntimeAssignmentOwnershipProof proof,
        Guid stepExecutionId,
        JsonElement input,
        CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeSideEffectAsync(proof, cancellationToken);
        if (authorization.Assignment.TargetKind != RuntimeAssignmentTargetKind.FlowRun)
            throw new RuntimeExecutionMaterialException("child_flow_not_allowed", "Only a root Flow assignment can create a child Flow.");
        var step = authorization.Assignment.StepExecutions.SingleOrDefault(value => value.Id == stepExecutionId)
            ?? throw new RuntimeExecutionMaterialException("execution_coordinate_invalid", "The child Flow does not reference an authorized StepExecution.");
        using var lease = await CreateLeaseGuardAsync(proof, authorization, cancellationToken);
        var child = await operations.CreateOrGetChildFlowAsync(proof.WorkspaceId, step.FlowRunId,
            step.StepDefinitionId, input, lease.Token);
        await assignments.RegisterChildFlowAsync(proof, child.RunId, lease.Token);
        var material = await materials.ResolveFlowRunAsync(authorization.Assignment, child.RunId, lease.Token);
        return child with { Material = material };
    }

    private async Task<(RuntimeAssignmentAuthorization Authorization, RuntimeExecutionMaterial Material, RuntimeExecutionAgentMaterial Agent)>
        AuthorizeParticipantSideEffectAsync(
            RuntimeAssignmentOwnershipProof proof,
            string participantId,
            Guid turnId,
            CancellationToken cancellationToken)
    {
        var authorization = await AuthorizeSideEffectAsync(proof, cancellationToken);
        var turn = authorization.Assignment.Turns.SingleOrDefault(value => value.Id == turnId)
            ?? throw new RuntimeExecutionMaterialException("execution_coordinate_invalid", "The model or Tool call references an unknown Turn.");
        var material = authorization.Assignment.TargetKind == RuntimeAssignmentTargetKind.FlowRun
            && turn.StepExecutionId is { } stepId
            ? await materials.ResolveFlowRunAsync(authorization.Assignment,
                authorization.Assignment.StepExecutions.Single(value => value.Id == stepId).FlowRunId, cancellationToken)
            : await materials.ResolveAsync(authorization.Assignment, cancellationToken);
        authorization = await AuthorizeSideEffectAsync(proof, cancellationToken);
        var agent = material switch
        {
            RuntimeDirectAgentExecutionMaterial direct when string.Equals(direct.Agent.ParticipantId, participantId, StringComparison.Ordinal) => direct.Agent,
            RuntimeRootFlowExecutionMaterial flow => flow.Agents.SingleOrDefault(value => string.Equals(value.ParticipantId, participantId, StringComparison.Ordinal)),
            _ => null
        } ?? throw new RuntimeExecutionMaterialException("participant_not_authorized", "The participant is not present in the assigned execution material.");
        return (authorization, material, agent);
    }

    private async Task<RuntimeAssignmentAuthorization> AuthorizeSideEffectAsync(
        RuntimeAssignmentOwnershipProof proof,
        CancellationToken cancellationToken)
    {
        leaseOptions.Validate();
        var authorization = await assignments.AuthorizeAsync(proof, cancellationToken);
        if (authorization.CancellationRequested || authorization.LeaseRemaining <= leaseOptions.MinimumSideEffectLeaseRemaining)
            throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.LeaseTooShort,
                "The assignment lease is too close to expiry to start a governed side effect.");
        return authorization;
    }

    private async Task<RuntimeAssignmentLeaseGuard> CreateLeaseGuardAsync(
        RuntimeAssignmentOwnershipProof proof,
        RuntimeAssignmentAuthorization authorization,
        CancellationToken cancellationToken)
    {
        var attempt = authorization.Assignment.CurrentAttempt
            ?? throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.NotOwned,
                "The assignment has no active attempt.");
        var guard = leaseGuards.Register(proof, attempt.LeaseExpiresAt, cancellationToken);
        try
        {
            var refreshed = await AuthorizeSideEffectAsync(proof, cancellationToken);
            attempt = refreshed.Assignment.CurrentAttempt
                ?? throw new RuntimeAssignmentException(RuntimeAssignmentErrorCodes.NotOwned,
                    "The assignment has no active attempt.");
            leaseGuards.Renew(proof, attempt.LeaseExpiresAt, refreshed.CancellationRequested);
            return guard;
        }
        catch
        {
            guard.Dispose();
            throw;
        }
    }

    private static long ModelPayloadLength(IEnumerable<RuntimeGovernedModelMessage> messages) => messages
        .SelectMany(value => value.Contents)
        .Sum(value => value switch
        {
            RuntimeGovernedModelText text => (long)text.Text.Length,
            RuntimeGovernedModelToolCall call => (long)call.CallId.Length + call.Name.Length + call.Arguments.GetRawText().Length,
            RuntimeGovernedModelToolResult result => (long)result.CallId.Length + result.Result.GetRawText().Length,
            _ => 0L
        });
}
