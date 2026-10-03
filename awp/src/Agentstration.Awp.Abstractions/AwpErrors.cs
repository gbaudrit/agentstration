using System.Text.Json;

namespace Agentstration.Awp.Abstractions;

public static class AwpErrorCodes
{
    public const string InvalidRequest = "invalid_request";
    public const string ProtocolVersionUnsupported = "protocol_version_unsupported";
    public const string RuntimeCapabilityIncompatible = "runtime_capability_incompatible";
    public const string ExecutionMaterialVersionUnsupported = "execution_material_version_unsupported";
    public const string WorkerNotRegistered = "worker_not_registered";
    public const string WorkerSessionSuperseded = "worker_session_superseded";
    public const string AssignmentNotFound = "assignment_not_found";
    public const string AssignmentNotOwned = "assignment_not_owned";
    public const string AssignmentLeaseExpired = "assignment_lease_expired";
    public const string AssignmentFenced = "assignment_fenced";
    public const string EventSequenceInvalid = "event_sequence_invalid";
    public const string TurnAttemptUnsupported = "turn_attempt_unsupported";
    public const string TerminalConflict = "terminal_conflict";
}

public sealed record AwpError(
    string Code,
    string Message,
    string? Target = null,
    IReadOnlyDictionary<string, JsonElement>? Details = null);
