using System.Text.Json.Serialization;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;

namespace Agentstration.Bootstrap.Contracts;

public static class WorkspaceSeedFormat
{
    public const string Version = "1.0";
    public const string ManifestPath = "agentstration-workspace-seed.json";
    public const int MaximumArchiveBytes = 4 * 1024 * 1024;
    public const int MaximumExpandedBytes = 32 * 1024 * 1024;
    public const int MaximumEntries = 8192;
    public const int MaximumDocumentBytes = 64 * 1024;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkspaceSeedPointer(string FormatVersion, Uri ArchiveUrl, string Sha256);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkspaceSeedManifest(string FormatVersion, string Version, string ProviderRevision,
    string Profile, string Path, WorkspaceSeedProject Project, WorkspaceSeedRequirements Requirements)
{
    public IReadOnlyList<WorkspaceSeedPlanImport> ImportPlans { get; init; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkspaceSeedProject(string Name, ResourceNamespace Namespace);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkspaceSeedRequirements(IReadOnlyList<BootstrapContributionSelector> Contributions,
    IReadOnlyList<ResourceReference> ModelProviders, IReadOnlyList<ResourceReference> RuntimeProfiles,
    WorkspaceSeedRuntimeCapability Runtime);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkspaceSeedRuntimeCapability(string Name, string CapabilityVersion, string MaterialVersion);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorkspaceSeedPlanImport(Guid PlanId, string ExpectedDigest);

/// <summary>Exactly one input supplies a full ZIP or a pointer document; the target is always explicit.</summary>
public sealed record IngestWorkspaceSeedRequest(Guid RequestId, BootstrapApplicationTarget Target,
    string? ArchiveBase64 = null, string? Document = null, Uri? DocumentUrl = null);

public sealed record WorkspaceSeedSelection(Guid Id, string ArchiveDigest);

public sealed record WorkspaceSeedProvenance(Guid Id, BootstrapApplicationTarget Target, Guid ActorPrincipalId,
    string Version, string ProviderRevision, string ArchiveDigest, string Profile, string Path,
    Uri? DocumentUrl, Uri? ArchiveUrl, string? DocumentDigest, DateTimeOffset IngestedAt)
{
    public IReadOnlyList<WorkspaceSeedPlanImport> ImportPlans { get; init; } = [];
}

public sealed record WorkspaceSeedReceipt(WorkspaceSeedProvenance Provenance, WorkspaceSeedManifest Manifest,
    BootstrapProfileSummary Profile);

public sealed record WorkspaceSeedPrerequisite(string Kind, string Name, bool Available, string? ReasonCode = null);

[JsonConverter(typeof(JsonStringEnumConverter<WorkspaceSeedHandoverStatus>))]
public enum WorkspaceSeedHandoverStatus { Pending, Succeeded, Failed }

public sealed record WorkspaceSeedHandover(Guid SeedId, WorkspaceSeedHandoverStatus Status,
    Guid ProjectId, string ProjectName, ResourceNamespace Namespace, string? Revision,
    IReadOnlyList<Guid> ImportedPlans, DateTimeOffset UpdatedAt, string? ErrorCode = null);
