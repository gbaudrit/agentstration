using Agentstration.Work;

namespace Agentstration.Work.Contracts;

public sealed record UpdateWorkOrganizationRequest(Guid Id, long ExpectedVersion, WorkOrganizationDefinition Definition);
public sealed record PublishWorkOrganizationRequest(Guid Id, long ExpectedOrganizationVersion, long ExpectedRecordVersion);
public sealed record ImportWorkOrganizationRequest(Guid Id, long ExpectedVersion, string ExpectedDigest);
public sealed record WorkOrganizationResponse(Guid ProjectId, long Version, WorkOrganizationDefinition Definition, DateTimeOffset UpdatedAt);

public static class WorkOrganizationContractMapping
{
    public static WorkOrganizationResponse ToResponse(WorkOrganizationSnapshot value) => new(value.ProjectId.Value, value.Version, value.Definition, value.UpdatedAt);
}
