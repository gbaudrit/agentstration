using System.Text.Json;
using Agentstration.Awp.Abstractions;
using Agentstration.Flows;

namespace Agentstration.Runtime.Worker.Core;

internal sealed record AwpWorkerRuntimeDescriptor(string SoftwareVersion, IReadOnlyList<AwpRuntimeCapability> Capabilities);
internal interface IAwpAssignmentExecutor
{
    Task<AwpAssignmentExecutionResult> ExecuteAsync(AwpAssignmentSession session, AwpExecutionMaterial material, CancellationToken cancellationToken);
}
internal sealed class AwpWaitingForInputException : Exception;

internal class AwpExecutionNotSupportedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
    public string? Details { get; init; }
    public FlowFailureOrigin? Origin { get; init; }
}

internal sealed class AwpExecutionTimedOutException(string code = "flow_run_timed_out", string message = "The Flow Run exceeded its execution timeout.")
    : AwpExecutionNotSupportedException(code, message);

internal sealed class AwpExecutionCancelledException(string details) : OperationCanceledException("The assigned native execution was cancelled.")
{
    internal string Details { get; } = details;
}

internal sealed record AwpAssignmentExecutionResult(
    JsonElement? Output,
    string? OutputName = null,
    string? Outcome = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? ErrorDetails = null,
    FlowFailureOrigin? ErrorOrigin = null);
