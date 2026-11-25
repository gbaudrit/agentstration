using Agentstration.Work;

namespace Agentstration.Work.Contracts;

public sealed record PreviewWorkDataRequest(WorkDataPackage? Package = null, string? Revision = null);
public sealed record ImportWorkDataRequest(Guid Id, string ReviewedDigest, WorkDataPackage? Package = null, string? Revision = null);
public sealed record WorkDataImportResponse(Guid Id, Guid ProjectId, WorkDataImportStatus Status, long Version,
    string ReviewDigest, string SourceDigest, string? SourceRevision, IReadOnlyList<WorkPlanId> ImportedPlans,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? ErrorCode);

public static class WorkDataContractMapping
{
    public static WorkDataImportResponse ToResponse(WorkDataImport value) => new(value.Id.Value, value.ProjectId.Value,
        value.Status, value.Version, value.ReviewDigest, value.SourceDigest, value.SourceRevision, value.ImportedPlans,
        value.CreatedAt, value.UpdatedAt, value.ErrorCode);
}
