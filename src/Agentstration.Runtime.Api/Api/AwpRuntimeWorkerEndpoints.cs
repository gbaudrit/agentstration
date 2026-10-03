using System.Security.Claims;
using Agentstration.Awp.Abstractions;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;
using Agentstration.Web.Security;

namespace Agentstration.Runtime.Api.Api;

public static class AwpRuntimeWorkerEndpoints
{
    public static IEndpointRouteBuilder MapAwpRuntimeWorkerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var awp = endpoints.MapGroup(AwpProtocol.BasePath)
            .RequireAuthorization(AgentstrationPolicies.AwpWorker);
        awp.MapPost("/workers/register", RegisterAsync)
            .Produces<AwpEnvelope<AwpWorkerRegistrationResponse>>()
            .WithSummary("Register or refresh an AWP Runtime Worker session");
        awp.MapPost("/assignments/claim", ClaimAsync)
            .Produces<AwpEnvelope<AwpClaimResponse>>()
            .WithSummary("Wait for and atomically claim compatible Runtime work");
        awp.MapPost("/assignments/heartbeat", HeartbeatAsync)
            .Produces<AwpEnvelope<AwpHeartbeatResponse>>()
            .WithSummary("Renew the current AWP assignment lease");
        awp.MapPost("/assignments/material", GetMaterialAsync)
            .Produces<AwpEnvelope<AwpGetExecutionMaterialResponse>>()
            .WithSummary("Resolve immutable execution material for an owned assignment");
        awp.MapPost("/assignments/steps/open", OpenStepExecutionAsync)
            .Produces<AwpEnvelope<AwpOpenStepExecutionResponse>>()
            .WithSummary("Open a server-authorized Flow StepExecution");
        awp.MapPost("/assignments/turns/open", OpenTurnAsync)
            .Produces<AwpEnvelope<AwpOpenTurnResponse>>()
            .WithSummary("Open a server-authorized Agent Turn and initial TurnAttempt");
        awp.MapPost("/assignments/events", AppendEventsAsync)
            .Produces<AwpEnvelope<AwpAppendEventsResponse>>()
            .WithSummary("Append an ordered idempotent execution-event batch");
        awp.MapPost("/assignments/checkpoints", StoreCheckpointAsync)
            .Produces<AwpEnvelope<AwpCheckpointResponse>>()
            .WithSummary("Persist a compatible assignment checkpoint");
        awp.MapPost("/assignments/checkpoints/get", GetCheckpointAsync)
            .Produces<AwpEnvelope<AwpCheckpointResponse>>()
            .WithSummary("Retrieve a compatible assignment checkpoint");
        awp.MapPost("/assignments/complete", CompleteAsync)
            .Produces<AwpEnvelope<AwpTerminalResponse>>()
            .WithSummary("Complete an owned assignment");
        awp.MapPost("/assignments/fail", FailAsync)
            .Produces<AwpEnvelope<AwpTerminalResponse>>()
            .WithSummary("Fail an owned assignment");
        awp.MapPost("/assignments/model/invoke", InvokeModelAsync)
            .Produces<AwpEnvelope<AwpInvokeModelResponse>>()
            .WithSummary("Invoke the assigned Agent model through the governed server boundary");
        awp.MapPost("/assignments/tools/invoke", InvokeToolAsync)
            .Produces<AwpEnvelope<AwpInvokeToolResponse>>()
            .WithSummary("Invoke an assigned Agent Tool through governance and audit hooks");
        awp.MapPost("/assignments/artifacts", StoreArtifactAsync)
            .Produces<AwpEnvelope<AwpArtifactResponse>>()
            .WithSummary("Store a bounded assignment Artifact without exposing its backend path");
        awp.MapPost("/assignments/artifacts/get", GetArtifactAsync)
            .Produces<AwpEnvelope<AwpArtifactResponse>>()
            .WithSummary("Read an assignment Artifact through its opaque identity");
        awp.MapPost("/assignments/child-flows", CreateChildFlowAsync)
            .Produces<AwpEnvelope<AwpChildFlowResponse>>()
            .WithSummary("Create or recover a child Flow declared by the assigned Flow snapshot");
        return endpoints;
    }

    private static async Task<IResult> RegisterAsync(
        AwpEnvelope<AwpWorkerRegistrationRequest> envelope,
        ClaimsPrincipal principal,
        RuntimeWorkerDispatchService dispatch,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) => await ExecuteAsync(async identity =>
    {
        ValidateEnvelope(envelope.ProtocolVersion, envelope.MessageId);
        var request = envelope.Payload;
        if (request?.Worker is null || request.SupportedProtocolVersions is null || request.Worker.Capabilities is null)
            throw new RuntimeWorkerDispatchException(AwpErrorCodes.InvalidRequest, "The Worker registration is incomplete.");
        if (request.Worker.Capabilities.Any(value => value is null || value.ExecutionMaterialVersions is null))
            throw new RuntimeWorkerDispatchException(AwpErrorCodes.InvalidRequest, "The Worker capability registration is incomplete.");
        if (request.SupportedProtocolVersions.Count is < 1 or > 16
            || !request.SupportedProtocolVersions.Contains(AwpProtocol.Version, StringComparer.Ordinal))
            throw new RuntimeWorkerDispatchException(AwpErrorCodes.ProtocolVersionUnsupported, "The Worker does not support AWP v1.");
        ValidateIdentity(identity, request.Worker.WorkerId, request.Worker.SessionId);
        var capabilities = request.Worker.Capabilities.Select(value => new RuntimeWorkerCapabilityRegistration(
            value.RuntimeKind,
            value.CapabilityVersion,
            value.ExecutionMaterialVersions.ToHashSet(StringComparer.Ordinal),
            value.RuntimeImplementationVersion)).ToArray();
        var registered = await dispatch.RegisterAsync(
            new(identity.WorkerId),
            new(identity.WorkerSessionId),
            request.Worker.SoftwareVersion,
            request.Worker.MaximumConcurrentAssignments,
            capabilities,
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        return Envelope(envelope.MessageId, now,
            new AwpWorkerRegistrationResponse(AwpProtocol.Version, registered.RegisteredAt, now));
    }, principal);

    private static async Task<IResult> ClaimAsync(
        AwpEnvelope<AwpClaimRequest> envelope,
        ClaimsPrincipal principal,
        RuntimeWorkerDispatchService dispatch,
        IRuntimeRunStore runs,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) => await ExecuteAsync(async identity =>
    {
        ValidateEnvelope(envelope.ProtocolVersion, envelope.MessageId);
        var request = envelope.Payload
            ?? throw new RuntimeWorkerDispatchException(AwpErrorCodes.InvalidRequest, "The claim request is required.");
        ValidateIdentity(identity, request.WorkerId, request.SessionId);
        var claimed = await dispatch.ClaimAsync(
            new(identity.WorkerId),
            new(identity.WorkerSessionId),
            request.AvailableCapacity,
            request.MaximumWaitSeconds,
            cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (claimed is null) return Envelope(envelope.MessageId, now, new AwpClaimResponse(now, null));
        var tenantId = claimed.Assignment.TenantId;
        if (claimed.Assignment.TargetKind == RuntimeAssignmentTargetKind.RuntimeRun)
        {
            var run = await runs.GetAsync(claimed.Assignment.WorkspaceId, claimed.Assignment.TargetRunId, cancellationToken)
                ?? throw new RuntimeRunNotFoundException(claimed.Assignment.TargetRunId);
            tenantId = run.Value.Scope.TenantId;
        }
        if (tenantId == Guid.Empty)
            throw new RuntimeWorkerDispatchException(AwpErrorCodes.InvalidRequest, "The assignment Tenant identity is missing.");
        return Envelope(envelope.MessageId, now, new AwpClaimResponse(now, ToContract(claimed, tenantId)));
    }, principal);

    private static async Task<IResult> HeartbeatAsync(
        AwpEnvelope<AwpHeartbeatRequest> envelope,
        ClaimsPrincipal principal,
        RuntimeWorkerDispatchService dispatch,
        RuntimeWorkerAssignmentService assignments,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) => await ExecuteAsync(async identity =>
    {
        ValidateEnvelope(envelope.ProtocolVersion, envelope.MessageId);
        var request = envelope.Payload;
        if (request?.Context is null)
            throw new RuntimeWorkerDispatchException(AwpErrorCodes.InvalidRequest, "The heartbeat ownership context is required.");
        ValidateIdentity(identity, request.Context.WorkerId, request.Context.SessionId);
        _ = dispatch.Touch(new(identity.WorkerId), new(identity.WorkerSessionId));
        var renewed = await assignments.HeartbeatAsync(ToProof(request.Context), cancellationToken);
        var now = timeProvider.GetUtcNow();
        return Envelope(envelope.MessageId, now, new AwpHeartbeatResponse(
            now,
            renewed.Assignment.CurrentAttempt!.LeaseExpiresAt,
            renewed.CancellationRequested
                ? new AwpCancellationDirective(true, renewed.Assignment.CancellationRequestedAt)
                : AwpCancellationDirective.None));
    }, principal);

    private static async Task<IResult> GetMaterialAsync(
        AwpEnvelope<AwpGetExecutionMaterialRequest> envelope,
        ClaimsPrincipal principal,
        RuntimeWorkerDispatchService dispatch,
        RuntimeWorkerExecutionService execution,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) => await ExecuteAsync(async identity =>
    {
        ValidateEnvelope(envelope.ProtocolVersion, envelope.MessageId);
        var context = envelope.Payload?.Context
            ?? throw new RuntimeWorkerDispatchException(AwpErrorCodes.InvalidRequest, "The execution-material ownership context is required.");
        ValidateCommand(identity, context, dispatch);
        var material = await execution.GetMaterialAsync(ToProof(context), cancellationToken);
        var now = timeProvider.GetUtcNow();
        return Envelope(envelope.MessageId, now, new AwpGetExecutionMaterialResponse(now, ToContract(material)));
    }, principal);

    private static async Task<IResult> OpenStepExecutionAsync(
        AwpEnvelope<AwpOpenStepExecutionRequest> envelope,
        ClaimsPrincipal principal,
        RuntimeWorkerDispatchService dispatch,
        RuntimeWorkerExecutionService execution,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) => await ExecuteAsync(async identity =>
    {
        ValidateEnvelope(envelope.ProtocolVersion, envelope.MessageId);
        var request = envelope.Payload
            ?? throw new RuntimeWorkerDispatchException(AwpErrorCodes.InvalidRequest, "The StepExecution request is required.");
        ValidateCommand(identity, request.Context, dispatch);
        var step = await execution.OpenStepExecutionAsync(ToProof(request.Context), envelope.MessageId, request.FlowRunId,
            request.FlowVersion, request.FlowDefinitionHash, request.StepDefinitionId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        return Envelope(envelope.MessageId, now, new AwpOpenStepExecutionResponse(now, ToContract(step)));
    }, principal);

    private static async Task<IResult> OpenTurnAsync(
        AwpEnvelope<AwpOpenTurnRequest> envelope,
        ClaimsPrincipal principal,
        RuntimeWorkerDispatchService dispatch,
        RuntimeWorkerExecutionService execution,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) => await ExecuteAsync(async identity =>
    {
        ValidateEnvelope(envelope.ProtocolVersion, envelope.MessageId);
        var request = envelope.Payload
            ?? throw new RuntimeWorkerDispatchException(AwpErrorCodes.InvalidRequest, "The Turn request is required.");
        ValidateCommand(identity, request.Context, dispatch);
        var turn = await execution.OpenTurnAsync(ToProof(request.Context), envelope.MessageId, request.RunId,
            request.FlowStep?.StepExecutionId.Value, request.ParticipantId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        return Envelope(envelope.MessageId, now, new AwpOpenTurnResponse(now,
            new(new(turn.Id), new(new(turn.AttemptId), turn.AttemptNumber), turn.ParticipantId)));
    }, principal);

    private static async Task<IResult> AppendEventsAsync(
        AwpEnvelope<AwpAppendEventsRequest> envelope,
        ClaimsPrincipal principal,
        RuntimeWorkerDispatchService dispatch,
        RuntimeWorkerExecutionService execution,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) => await ExecuteAsync(async identity =>
    {
        ValidateEnvelope(envelope.ProtocolVersion, envelope.MessageId);
        var request = envelope.Payload
            ?? throw new RuntimeWorkerDispatchException(AwpErrorCodes.InvalidRequest, "The event batch is required.");
        ValidateCommand(identity, request.Context, dispatch);
        var events = request.Events.Select(value => new RuntimeAssignmentExecutionEvent
        {
            EventId = value.EventId.Value,
            AttemptEventSequence = value.AttemptEventSequence,
            OccurredAt = value.OccurredAt,
            Kind = value.Kind.ToString(),
            RunId = value.Location.RunId,
            StepExecutionId = value.Location.FlowStep?.StepExecutionId.Value,
            TurnId = value.Location.AgentTurn?.TurnId.Value,
            TurnAttemptId = value.Location.AgentTurn?.Attempt.TurnAttemptId.Value,
            ToolCallId = value.ToolCallId?.Value,
            Payload = value.Payload?.Clone()
        }).ToArray();
        var result = await execution.AppendEventsAsync(ToProof(request.Context), events, cancellationToken);
        var now = timeProvider.GetUtcNow();
        return Envelope(envelope.MessageId, now, new AwpAppendEventsResponse(now, result.AcceptedThroughSequence,
            result.DuplicateEventIds.Select(value => new AwpEventId(value)).ToArray()));
    }, principal);

    private static async Task<IResult> StoreCheckpointAsync(
        AwpEnvelope<AwpStoreCheckpointRequest> envelope,
        ClaimsPrincipal principal,
        RuntimeWorkerDispatchService dispatch,
        RuntimeWorkerExecutionService execution,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) => await ExecuteAsync(async identity =>
    {
        ValidateEnvelope(envelope.ProtocolVersion, envelope.MessageId);
        var request = envelope.Payload
            ?? throw new RuntimeWorkerDispatchException(AwpErrorCodes.InvalidRequest, "The checkpoint is required.");
        ValidateCommand(identity, request.Context, dispatch);
        var checkpoint = await execution.StoreCheckpointAsync(ToProof(request.Context), request.CheckpointId,
            request.SchemaVersion, request.CompatibilityKey, request.Payload, cancellationToken);
        var now = timeProvider.GetUtcNow();
        return Envelope(envelope.MessageId, now, ToContract(now, checkpoint));
    }, principal);

    private static async Task<IResult> GetCheckpointAsync(
        AwpEnvelope<AwpGetCheckpointRequest> envelope,
        ClaimsPrincipal principal,
        RuntimeWorkerDispatchService dispatch,
        RuntimeWorkerExecutionService execution,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) => await ExecuteAsync(async identity =>
    {
        ValidateEnvelope(envelope.ProtocolVersion, envelope.MessageId);
        var request = envelope.Payload
            ?? throw new RuntimeWorkerDispatchException(AwpErrorCodes.InvalidRequest, "The checkpoint request is required.");
        ValidateCommand(identity, request.Context, dispatch);
        var checkpoint = await execution.GetCheckpointAsync(ToProof(request.Context), request.CheckpointId, cancellationToken)
            ?? throw new RuntimeExecutionMaterialException("checkpoint_not_found", "The checkpoint was not found.");
        var now = timeProvider.GetUtcNow();
        return Envelope(envelope.MessageId, now, ToContract(now, checkpoint));
    }, principal);

    private static async Task<IResult> CompleteAsync(
        AwpEnvelope<AwpCompleteAssignmentRequest> envelope,
        ClaimsPrincipal principal,
        RuntimeWorkerDispatchService dispatch,
        RuntimeWorkerAssignmentService assignments,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) => await ExecuteAsync(async identity =>
    {
        ValidateEnvelope(envelope.ProtocolVersion, envelope.MessageId);
        var request = envelope.Payload
            ?? throw new RuntimeWorkerDispatchException(AwpErrorCodes.InvalidRequest, "The completion request is required.");
        ValidateCommand(identity, request.Context, dispatch);
        var result = await assignments.CompleteAsync(ToProof(request.Context), new RuntimeAssignmentTerminalCommand(
            RuntimeAssignmentTerminalOutcome.Succeeded, request.CommandId.Value, request.Output?.GetRawText()), cancellationToken);
        var now = timeProvider.GetUtcNow();
        return Envelope(envelope.MessageId, now, new AwpTerminalResponse(now, ToContract(result.RunState),
            result.Assignment.ExecutionEvents.LastOrDefault()?.AttemptEventSequence ?? 0));
    }, principal);

    private static async Task<IResult> FailAsync(
        AwpEnvelope<AwpFailAssignmentRequest> envelope,
        ClaimsPrincipal principal,
        RuntimeWorkerDispatchService dispatch,
        RuntimeWorkerAssignmentService assignments,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) => await ExecuteAsync(async identity =>
    {
        ValidateEnvelope(envelope.ProtocolVersion, envelope.MessageId);
        var request = envelope.Payload
            ?? throw new RuntimeWorkerDispatchException(AwpErrorCodes.InvalidRequest, "The failure request is required.");
        ValidateCommand(identity, request.Context, dispatch);
        var result = await assignments.CompleteAsync(ToProof(request.Context), new RuntimeAssignmentTerminalCommand(
            RuntimeAssignmentTerminalOutcome.Failed, request.CommandId.Value, Error: request.Failure.Message,
            ErrorCode: request.Failure.Code), cancellationToken);
        var now = timeProvider.GetUtcNow();
        return Envelope(envelope.MessageId, now, new AwpTerminalResponse(now, ToContract(result.RunState),
            result.Assignment.ExecutionEvents.LastOrDefault()?.AttemptEventSequence ?? 0));
    }, principal);

    private static async Task<IResult> InvokeModelAsync(
        AwpEnvelope<AwpInvokeModelRequest> envelope,
        ClaimsPrincipal principal,
        RuntimeWorkerDispatchService dispatch,
        RuntimeWorkerExecutionService execution,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) => await ExecuteAsync(async identity =>
    {
        ValidateEnvelope(envelope.ProtocolVersion, envelope.MessageId);
        var request = envelope.Payload
            ?? throw new RuntimeWorkerDispatchException(AwpErrorCodes.InvalidRequest, "The model invocation request is required.");
        ValidateCommand(identity, request.Context, dispatch);
        var messages = request.Messages.Select(value => new RuntimeGovernedModelMessage(
            ParseRole(value.Role), value.Contents.Select(ToRuntimeContent).ToArray())).ToArray();
        var response = await execution.InvokeModelAsync(ToProof(request.Context), request.ParticipantId,
            request.TurnId.Value, request.TurnAttemptId.Value, messages, cancellationToken);
        var now = timeProvider.GetUtcNow();
        return Envelope(envelope.MessageId, now, new AwpInvokeModelResponse(now,
            response.Contents.Select(ToContract).ToArray(), response.ModelId,
            response.FinishReason, response.InputTokens, response.OutputTokens));
    }, principal);

    private static async Task<IResult> InvokeToolAsync(
        AwpEnvelope<AwpInvokeToolRequest> envelope,
        ClaimsPrincipal principal,
        RuntimeWorkerDispatchService dispatch,
        RuntimeWorkerExecutionService execution,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) => await ExecuteAsync(async identity =>
    {
        ValidateEnvelope(envelope.ProtocolVersion, envelope.MessageId);
        var request = envelope.Payload
            ?? throw new RuntimeWorkerDispatchException(AwpErrorCodes.InvalidRequest, "The Tool invocation request is required.");
        ValidateCommand(identity, request.Context, dispatch);
        var result = await execution.InvokeToolAsync(ToProof(request.Context), request.ParticipantId,
            request.TurnId.Value, request.TurnAttemptId.Value, request.ToolCallId.Value,
            request.ToolId, request.Arguments, cancellationToken);
        var now = timeProvider.GetUtcNow();
        return Envelope(envelope.MessageId, now, new AwpInvokeToolResponse(now, result));
    }, principal);

    private static async Task<IResult> StoreArtifactAsync(
        AwpEnvelope<AwpStoreArtifactRequest> envelope,
        ClaimsPrincipal principal,
        RuntimeWorkerDispatchService dispatch,
        RuntimeWorkerExecutionService execution,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) => await ExecuteAsync(async identity =>
    {
        ValidateEnvelope(envelope.ProtocolVersion, envelope.MessageId);
        var request = envelope.Payload
            ?? throw new RuntimeWorkerDispatchException(AwpErrorCodes.InvalidRequest, "The Artifact request is required.");
        ValidateCommand(identity, request.Context, dispatch);
        var artifact = await execution.StoreArtifactAsync(ToProof(request.Context), request.Name,
            request.ContentType, request.Content, cancellationToken);
        var now = timeProvider.GetUtcNow();
        return Envelope(envelope.MessageId, now, ToContract(now, artifact));
    }, principal);

    private static async Task<IResult> GetArtifactAsync(
        AwpEnvelope<AwpGetArtifactRequest> envelope,
        ClaimsPrincipal principal,
        RuntimeWorkerDispatchService dispatch,
        RuntimeWorkerExecutionService execution,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) => await ExecuteAsync(async identity =>
    {
        ValidateEnvelope(envelope.ProtocolVersion, envelope.MessageId);
        var request = envelope.Payload
            ?? throw new RuntimeWorkerDispatchException(AwpErrorCodes.InvalidRequest, "The Artifact read request is required.");
        ValidateCommand(identity, request.Context, dispatch);
        var artifact = await execution.GetArtifactAsync(ToProof(request.Context), request.ArtifactId, cancellationToken)
            ?? throw new RuntimeExecutionMaterialException("artifact_not_found", "The assignment Artifact was not found.");
        var now = timeProvider.GetUtcNow();
        return Envelope(envelope.MessageId, now, ToContract(now, artifact));
    }, principal);

    private static async Task<IResult> CreateChildFlowAsync(
        AwpEnvelope<AwpCreateChildFlowRequest> envelope,
        ClaimsPrincipal principal,
        RuntimeWorkerDispatchService dispatch,
        RuntimeWorkerExecutionService execution,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) => await ExecuteAsync(async identity =>
    {
        ValidateEnvelope(envelope.ProtocolVersion, envelope.MessageId);
        var request = envelope.Payload
            ?? throw new RuntimeWorkerDispatchException(AwpErrorCodes.InvalidRequest, "The child Flow request is required.");
        ValidateCommand(identity, request.Context, dispatch);
        var child = await execution.CreateOrGetChildFlowAsync(ToProof(request.Context), request.StepExecutionId.Value,
            request.Input, cancellationToken);
        var now = timeProvider.GetUtcNow();
        return Envelope(envelope.MessageId, now, new AwpChildFlowResponse(now, child.RunId, child.Status, child.Output));
    }, principal);

    private static async Task<IResult> ExecuteAsync<T>(
        Func<AwpWorkerRequestIdentity, Task<T>> action,
        ClaimsPrincipal principal)
    {
        if (!AwpWorkerAuthentication.TryGetIdentity(principal, out var identity)) return Results.Unauthorized();
        try { return Results.Ok(await action(identity)); }
        catch (RuntimeWorkerDispatchException exception)
        {
            var status = exception.Code is AwpErrorCodes.WorkerNotRegistered or AwpErrorCodes.WorkerSessionSuperseded
                ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest;
            return Problem(status, exception.Code, exception.Message);
        }
        catch (RuntimeAssignmentException exception)
        {
            var code = exception.Code switch
            {
                RuntimeAssignmentErrorCodes.LeaseExpired => AwpErrorCodes.AssignmentLeaseExpired,
                RuntimeAssignmentErrorCodes.FencingRejected => AwpErrorCodes.AssignmentFenced,
                RuntimeAssignmentErrorCodes.TerminalConflict => AwpErrorCodes.TerminalConflict,
                RuntimeAssignmentErrorCodes.InvalidCoordinate => AwpErrorCodes.ExecutionCoordinateInvalid,
                RuntimeAssignmentErrorCodes.InvalidEventSequence => AwpErrorCodes.EventSequenceInvalid,
                RuntimeAssignmentErrorCodes.ReplayConflict => AwpErrorCodes.ReplayConflict,
                RuntimeAssignmentErrorCodes.LimitExceeded => AwpErrorCodes.LimitExceeded,
                RuntimeAssignmentErrorCodes.LeaseTooShort => AwpErrorCodes.AssignmentLeaseTooShort,
                _ => AwpErrorCodes.AssignmentNotOwned
            };
            return Problem(StatusCodes.Status409Conflict, code, exception.Message);
        }
        catch (RuntimeExecutionMaterialException exception)
        {
            return Problem(StatusCodes.Status409Conflict, exception.Code, exception.Message);
        }
    }

    private static object Envelope<T>(Guid messageId, DateTimeOffset sentAt, T payload) =>
        new AwpEnvelope<T>(AwpProtocol.Version, messageId, sentAt, payload);

    private static void ValidateEnvelope(string protocolVersion, Guid messageId)
    {
        if (!string.Equals(protocolVersion, AwpProtocol.Version, StringComparison.Ordinal))
            throw new RuntimeWorkerDispatchException(AwpErrorCodes.ProtocolVersionUnsupported, "The AWP protocol version is unsupported.");
        if (messageId == Guid.Empty)
            throw new RuntimeWorkerDispatchException(AwpErrorCodes.InvalidRequest, "The AWP message identity must not be empty.");
    }

    private static void ValidateIdentity(
        AwpWorkerRequestIdentity identity,
        AwpWorkerId workerId,
        AwpWorkerSessionId sessionId)
    {
        if (identity.WorkerId != workerId.Value || identity.WorkerSessionId != sessionId.Value)
            throw new RuntimeWorkerDispatchException(AwpErrorCodes.WorkerSessionSuperseded,
                "The request Worker identity does not match the authenticated Worker session.");
    }

    private static void ValidateCommand(
        AwpWorkerRequestIdentity identity,
        AwpAssignmentCommandContext context,
        RuntimeWorkerDispatchService dispatch)
    {
        ValidateIdentity(identity, context.WorkerId, context.SessionId);
        _ = dispatch.Touch(new(identity.WorkerId), new(identity.WorkerSessionId));
    }

    private static RuntimeAssignmentOwnershipProof ToProof(AwpAssignmentCommandContext context) => new()
    {
        WorkspaceId = new WorkspaceId(ParseWorkspaceId(context.Scope.WorkspaceId)),
        AssignmentId = new RuntimeAssignmentId(context.AssignmentId.Value),
        AttemptId = new RuntimeAssignmentAttemptId(context.AttemptId.Value),
        WorkerId = new RuntimeWorkerId(context.WorkerId.Value),
        WorkerSessionId = new RuntimeWorkerSessionId(context.SessionId.Value),
        FencingGeneration = context.FencingGeneration,
        OwnershipToken = context.OwnershipToken
    };

    private static Guid ParseWorkspaceId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var workspaceId) || workspaceId == Guid.Empty)
            throw new RuntimeWorkerDispatchException(AwpErrorCodes.InvalidRequest, "The assignment Workspace identity is invalid.");
        return workspaceId;
    }

    private static AwpRunAssignment ToContract(ClaimedRuntimeWorkerAssignment claimed, Guid tenantId)
    {
        var assignment = claimed.Assignment;
        var attempt = assignment.CurrentAttempt!;
        return new AwpRunAssignment(
            new(assignment.Id.Value),
            new(attempt.Id.Value),
            new(tenantId, assignment.WorkspaceId.Value.ToString("D")),
            assignment.TargetKind == RuntimeAssignmentTargetKind.FlowRun
                ? new AwpRootFlowRunTarget(assignment.TargetRunId)
                : new AwpDirectAgentRunTarget(assignment.TargetRunId),
            new(assignment.RuntimeCapability, assignment.RuntimeCapabilityVersion, assignment.ExecutionMaterialVersion),
            new(assignment.ExecutionMaterialId, assignment.ExecutionMaterialVersion, assignment.ExecutionMaterialDigest),
            new(claimed.Ownership.OwnershipToken, attempt.FencingGeneration, attempt.LeaseExpiresAt,
                checked((int)claimed.HeartbeatInterval.TotalSeconds)));
    }

    private static AwpFlowStepLocation ToContract(RuntimeAssignmentStepExecution step) => new(
        step.FlowRunId,
        step.FlowVersion,
        step.FlowDefinitionHash,
        step.StepDefinitionId,
        step.StepName,
        step.StepType,
        step.DefinitionPosition,
        new(step.Id));

    private static AwpExecutionMaterial ToContract(RuntimeExecutionMaterial material) => material switch
    {
        RuntimeDirectAgentExecutionMaterial direct => new AwpDirectAgentExecutionMaterial(
            direct.MaterialId,
            direct.SchemaVersion,
            direct.Digest,
            direct.RunId,
            direct.Input.Messages.Select(value => new AwpExecutionMessage(value.Role.ToString(), value.Content)).ToArray(),
            direct.Input.Context,
            new AwpExecutionOptions(direct.Execution.TimeoutSeconds, direct.Execution.Streaming.ToString(),
                direct.Execution.PersistToolArguments, direct.Execution.Parameters),
            ToContract(direct.Agent)),
        RuntimeRootFlowExecutionMaterial flow => new AwpRootFlowExecutionMaterial(
            flow.MaterialId,
            flow.SchemaVersion,
            flow.Digest,
            flow.RunId,
            flow.FlowId,
            flow.FlowNamespace,
            flow.FlowVersion,
            flow.FlowDefinitionHash,
            flow.Input,
            flow.Definition,
            flow.Agents.Select(ToContract).ToArray()),
        _ => throw new ArgumentOutOfRangeException(nameof(material))
    };

    private static AwpExecutionAgentMaterial ToContract(RuntimeExecutionAgentMaterial agent) => new(
        agent.MaterialId,
        agent.ParticipantId,
        agent.AgentId,
        agent.AgentName,
        agent.Generation,
        agent.RevisionId,
        agent.DefinitionHash,
        agent.Handler,
        agent.DisplayName,
        agent.Description,
        agent.Instructions,
        agent.ModelProfileName,
        agent.ModelProfileNamespace.ToString(),
        agent.Tools.Select(value => new AwpExecutionToolMaterial(
            value.Id,
            value.Namespace.ToString(),
            value.Name,
            value.Description,
            value.InputSchema,
            value.OutputSchema,
            value.RequiresApproval)).ToArray());

    private static AwpCheckpointResponse ToContract(DateTimeOffset serverTime, RuntimeAssignmentCheckpoint checkpoint) => new(
        serverTime,
        checkpoint.CheckpointId,
        checkpoint.SchemaVersion,
        checkpoint.CompatibilityKey,
        checkpoint.Payload,
        checkpoint.PersistedAt);

    private static AwpArtifactResponse ToContract(DateTimeOffset serverTime, RuntimeGovernedArtifact artifact) => new(
        serverTime, artifact.ArtifactId, artifact.Name, artifact.ContentType, artifact.Length, artifact.Content);

    private static RuntimeMessageRole ParseRole(string role) => Enum.TryParse<RuntimeMessageRole>(role, true, out var value)
        ? value
        : throw new RuntimeWorkerDispatchException(AwpErrorCodes.InvalidRequest, $"Unsupported model message role '{role}'.");

    private static RuntimeGovernedModelContent ToRuntimeContent(AwpModelContent content) => content switch
    {
        AwpModelTextContent text => new RuntimeGovernedModelText(text.Text),
        AwpModelToolCallContent call => new RuntimeGovernedModelToolCall(call.CallId, call.Name, call.Arguments.Clone()),
        AwpModelToolResultContent result => new RuntimeGovernedModelToolResult(result.CallId, result.Result.Clone()),
        _ => throw new RuntimeWorkerDispatchException(AwpErrorCodes.InvalidRequest, "Unsupported model content kind.")
    };

    private static AwpModelContent ToContract(RuntimeGovernedModelContent content) => content switch
    {
        RuntimeGovernedModelText text => new AwpModelTextContent(text.Text),
        RuntimeGovernedModelToolCall call => new AwpModelToolCallContent(call.CallId, call.Name, call.Arguments),
        RuntimeGovernedModelToolResult result => new AwpModelToolResultContent(result.CallId, result.Result),
        _ => throw new ArgumentOutOfRangeException(nameof(content))
    };

    private static AwpTerminalState ToContract(RuntimeRunState state) => state switch
    {
        RuntimeRunState.Succeeded => AwpTerminalState.Succeeded,
        RuntimeRunState.Cancelled => AwpTerminalState.Cancelled,
        _ => AwpTerminalState.Failed
    };

    private static IResult Problem(int status, string code, string message) =>
        Results.Problem(statusCode: status, title: message,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}
