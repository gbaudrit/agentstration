using Agentstration.Resources;
namespace Agentstration.Work;

public readonly record struct WorkDataImportId(Guid Value)
{
    public override string ToString() => Value.ToString("D");
}

public sealed record WorkDataFile(string Path, string Content);
public sealed record WorkDataPackage(string? Revision, IReadOnlyList<WorkDataFile> Files);
public sealed record WorkDataMetadata(string Name);
public sealed record WorkDataManifest<T>(string ApiVersion, string Kind, WorkDataMetadata Metadata, T Definition);
public sealed record WorkDataCatalogDefinition(string FormatVersion, WorkSharingProjectId ProjectId, IReadOnlyList<string> Plans)
{
    public IReadOnlyList<string>? Epics { get; init; }
}
public sealed record WorkDataPlanDefinition(WorkPlanId Id, WorkPlanDefinition Plan, IReadOnlyList<string> TaskTrees)
{
    public WorkSharingIssueReference? Issue { get; init; }
}

public sealed record WorkDataDiagnostic(string Path, string Code);
public sealed record WorkDataPlanSelection(string Path, WorkSharingPlan Plan, long LocalVersion);
public sealed record WorkDataTaskObservation(WorkTaskId Id, long Version);
public sealed record WorkDataSelection(WorkSharingProjectId ProjectId, IReadOnlyList<WorkDataPlanSelection> Plans)
{
    public WorkOrganizationDefinition? Organization { get; init; }
    public long OrganizationVersion { get; init; }
}
public sealed record WorkDataPreview(bool CanImport, long ProjectVersion, string? Revision, string? SourceDigest,
    string? Digest, IReadOnlyList<WorkDataPlanSelection> Plans, IReadOnlyList<WorkDataDiagnostic> Diagnostics)
{
    public WorkOrganizationDefinition? Organization { get; init; }
    public long OrganizationVersion { get; init; }
}

public enum WorkDataImportStatus { Pending, Partial, Succeeded }
public sealed record WorkDataImport(WorkDataImportId Id, WorkspaceId WorkspaceId, Guid PrincipalId, WorkProjectId ProjectId,
    long ProjectVersion, string ProjectContextDigest, string RequestDigest, string ReviewDigest, string SourceDigest, string? SourceRevision,
    WorkDataSelection Selection, IReadOnlyList<WorkDataTaskObservation> Tasks, IReadOnlyList<WorkPlanId> ImportedPlans,
    WorkDataImportStatus Status, long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? ErrorCode = null);

public static class WorkDataFormat
{
    public const string Version = "1.0";
    public const string CatalogPath = "catalog.yaml";
    public const int MaximumFiles = 128;
    public const int MaximumFileBytes = 16 * 1024;
    public const int MaximumPackageBytes = 256 * 1024;
    public const int MaximumPlans = 16;

    public static bool ValidPath(string? path) => path is { Length: > 0 and <= 256 }
        && path.EndsWith(".yaml", StringComparison.Ordinal)
        && path.Split('/').All(segment => segment is not ("" or "." or "..")
            && segment.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '-' or '_' or '.'));

    public static bool PlanPath(string? path) => ValidPath(path)
        && (path!.StartsWith("plans/", StringComparison.Ordinal) || path.StartsWith("epics/", StringComparison.Ordinal))
        && path.EndsWith("/plan.yaml", StringComparison.Ordinal);
}
