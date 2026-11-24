using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.Work;

public readonly record struct WorkTaskOperationId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

[JsonConverter(typeof(JsonStringEnumConverter<ExternalTaskOperationKind>))]
public enum ExternalTaskOperationKind { Read, Update, Publish }
public enum WorkTaskOperationStatus { Pending, Uncertain, Succeeded, Rejected }
[JsonConverter(typeof(JsonStringEnumConverter<ExternalTaskProviderStatus>))]
public enum ExternalTaskProviderStatus { Missing, Succeeded, Rejected, Uncertain }
public sealed record ExternalTaskRelation(string Kind, string Repository, string TaskId);

public sealed record ExternalTaskOperationInput(ExternalTaskOperationKind Operation, string Repository, string? TaskId = null,
    string? Title = null, string? Body = null, string? State = null,
    IReadOnlyList<string>? Labels = null, IReadOnlyList<ExternalTaskRelation>? Relations = null, string? ExpectedRevision = null)
{
    public Result<ExternalTaskOperationInput> Validate()
    {
        if (!Enum.IsDefined(Operation) || !Text(Repository, 512) || Repository.Contains('@', StringComparison.Ordinal)
            || ExpectedRevision?.Length is > 256 || Operation == ExternalTaskOperationKind.Update && !Text(ExpectedRevision, 256)
            || Operation != ExternalTaskOperationKind.Update && ExpectedRevision is not null
            || TaskId?.Length is > 256 || Title is not null && !Text(Title, 512) || Body?.Length is > 32_768 || State is not null && !Text(State, 64)
            || Labels?.Count > 32 || Labels?.Any(label => !Text(label, 128)) == true
            || Relations?.Count > 100 || Relations?.Any(relation => relation is null || !Text(relation.Kind, 64)
                || !Text(relation.Repository, 512) || !Text(relation.TaskId, 256)) == true
            || (Operation == ExternalTaskOperationKind.Publish ? !Text(Title, 512) || TaskId is not null : !Text(TaskId, 256))
            || Operation == ExternalTaskOperationKind.Read && (Title is not null || Body is not null || State is not null || Labels is not null || Relations is not null))
            return Result<ExternalTaskOperationInput>.Failure("task_provider_input_invalid");
        return Result<ExternalTaskOperationInput>.Success(this);
    }

    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
}

public sealed record ExternalTaskSnapshot(string Repository, string TaskId, string Revision, string Title, string? Body, string State,
    Uri? Url, IReadOnlyList<string> Labels, IReadOnlyList<ExternalTaskRelation> Relations);
public sealed record WorkTaskProviderCapabilities(string Version, IReadOnlyList<ExternalTaskOperationKind> Operations,
    bool IdempotentOperations, bool OperationRecovery);
public sealed record WorkTaskProviderRoute(WorkProjectContextBinding Binding, WorkProjectResourceReference Tool,
    WorkTaskProviderCapabilities Capabilities)
{
    public WorkProjectResourceReference? Provider { get; init; }
}
public sealed record ExternalTaskProviderRequest(string Mode, string OperationKey, string IntentDigest, ExternalTaskOperationInput Input);
public sealed record ExternalTaskProviderResponse(string OperationKey, string IntentDigest, ExternalTaskProviderStatus Status,
    ExternalTaskSnapshot? Task = null, string? ErrorCode = null);

public sealed record WorkTaskOperation(WorkTaskOperationId Id, WorkspaceId WorkspaceId, Guid OwnerPrincipalId, Guid TenantId,
    WorkProjectId ProjectId, long ProjectVersion, WorkPlanId? PlanId, long? PlanRevision, string? PlanDigest,
    ExternalTaskOperationInput Input, string IntentDigest, string OperationKey, WorkTaskProviderRoute Route,
    WorkTaskOperationStatus Status, long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    ExternalTaskSnapshot? Task = null, string? ErrorCode = null);
public sealed record WorkTaskOperationPage(IReadOnlyList<WorkTaskOperation> Items, bool HasMore);

public static class WorkTaskProviderProtocol
{
    public const string MetadataKey = "agentstration.work.task-provider";
    public const string Version = "1.0";
}
