using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;

namespace Agentstration.Knowledge.Contracts;

public sealed record KnowledgeContributionSelection(string BindingName, Guid DataSourceUid);

public sealed record KnowledgeToolProposal
{
    public required string Route { get; init; }
    public required string DefinitionName { get; init; }
    public required KnowledgeSourceOperation Operation { get; init; }
    public KnowledgeContributionSelection? Contribution { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public string? BusinessDescription { get; init; }
    public string? Usage { get; init; }
    public required JsonElement InputSchema { get; init; }
    public JsonElement? OutputSchema { get; init; }
    public JsonElement? DataSchema { get; init; }
    public string ContentEvidence { get; init; } = "not_checked";
    public StorageQueryProfilePin? Profile { get; init; }
    public string? Issue { get; init; }
    public string Change { get; init; } = "added";
    public required string ContractDigest { get; init; }
}

public sealed record KnowledgeSourceToolProposal
{
    public required Guid SourceUid { get; init; }
    public required long SourceGeneration { get; init; }
    public required string Digest { get; init; }
    public required string ToolSetName { get; init; }
    public string? PublishedVersion { get; init; }
    public bool Ready { get; init; }
    public IReadOnlyList<string> Issues { get; init; } = [];
    public IReadOnlyList<KnowledgeToolProposal> Tools { get; init; } = [];
}

public sealed record KnowledgeToolExposureVersion : Resource, IImmutableResource
{
    public required Guid SourceUid { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public required string Version { get; init; }
    public required KnowledgeSourceToolProposal Proposal { get; init; }
    public IReadOnlyList<KnowledgeSourceToolOperationExposure> Operations { get; init; } = [];
}

public sealed record KnowledgeDescribeRequest(string Exposure, string? SnapshotId = null, string? ExposureScope = null, string? Input = null);
public sealed record KnowledgeContributionDescription(KnowledgeContributionSelection Selection,
    string State, IReadOnlyList<string> DocumentIds, IReadOnlyList<JsonElement> Schemas,
    string? AcquisitionId, DateTimeOffset? AcquiredAt)
{
    public string? Description { get; init; }
    public string DescriptionSource { get; init; } = "published";
    public IReadOnlyList<string> MediaTypes { get; init; } = [];
}
public sealed record KnowledgeToolSummary(string Route, KnowledgeSourceOperation Operation, string DisplayName,
    string? Description, KnowledgeContributionSelection? Contribution);
public sealed record KnowledgeStructureDescription(string DisplayName, string? Description, string ExposureVersion,
    string ProposalDigest, string? SnapshotId, string State, KnowledgeContentAvailability ContentAvailability,
    IReadOnlyList<KnowledgeToolProposal> Tools, IReadOnlyList<KnowledgeSourceToolOperationExposure> Access,
    IReadOnlyList<KnowledgeContributionDescription> Contributions)
{
    public string DetailLevel { get; init; } = "overview";
    public IReadOnlyList<KnowledgeToolSummary> ToolSummaries { get; init; } = [];
    public string RelationshipEvidence { get; init; } = "not_declared";
}
