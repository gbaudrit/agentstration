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
        var run = await runs.GetAsync(claimed.Assignment.WorkspaceId, claimed.Assignment.TargetRunId, cancellationToken)
            ?? throw new RuntimeRunNotFoundException(claimed.Assignment.TargetRunId);
        return Envelope(envelope.MessageId, now, new AwpClaimResponse(now, ToContract(claimed, run.Value)));
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
                _ => AwpErrorCodes.AssignmentNotOwned
            };
            return Problem(StatusCodes.Status409Conflict, code, exception.Message);
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

    private static AwpRunAssignment ToContract(ClaimedRuntimeWorkerAssignment claimed, RuntimeRun run)
    {
        var assignment = claimed.Assignment;
        var attempt = assignment.CurrentAttempt!;
        return new AwpRunAssignment(
            new(assignment.Id.Value),
            new(attempt.Id.Value),
            new(run.Scope.TenantId, assignment.WorkspaceId.Value.ToString("D")),
            new AwpDirectAgentRunTarget(assignment.TargetRunId),
            new(assignment.RuntimeCapability, assignment.RuntimeCapabilityVersion, assignment.ExecutionMaterialVersion),
            new(assignment.ExecutionMaterialId, assignment.ExecutionMaterialVersion, assignment.ExecutionMaterialDigest),
            new(claimed.Ownership.OwnershipToken, attempt.FencingGeneration, attempt.LeaseExpiresAt,
                checked((int)claimed.HeartbeatInterval.TotalSeconds)));
    }

    private static IResult Problem(int status, string code, string message) =>
        Results.Problem(statusCode: status, title: message,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}
