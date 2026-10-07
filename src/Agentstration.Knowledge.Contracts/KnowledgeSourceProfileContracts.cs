using System.Text.Json;
using Agentstration.Resources;

namespace Agentstration.Knowledge.Contracts;

public static class KnowledgeSourceProfileBuiltIns
{
    public const string Web = "web-builtin";
    public const string Rest = "rest-builtin";
    public const string ArtifactImport = "artifact-import-builtin";
    public const string Origin = "core";
    public const string Owner = "agentstration.knowledge";

    public static bool IsReserved(string name) => name is Web or Rest or ArtifactImport;
}

public sealed record KnowledgeSourceProfileToolBinding
{
    public required string Name { get; init; }
    public required string Capability { get; init; }
    public required ResourceReference Tool { get; init; }
}

public sealed record KnowledgeSourceProfileToolSetRoute
{
    public required string Name { get; init; }
    public required ResourceReference ToolSet { get; init; }
    public required string Version { get; init; }
    public required string Capability { get; init; }
    public string? Route { get; init; }
}

public sealed record KnowledgeSourceProfileStorageFlow
{
    public required string Role { get; init; }
    public required KnowledgeFlowTarget Flow { get; init; }
}

public sealed record KnowledgeSourceProfileApplicationProvenance
{
    public required string ProfileName { get; init; }
    public required ResourceNamespace ProfileNamespace { get; init; }
    public required Guid ProfileUid { get; init; }
    public required string Version { get; init; }
    public required string DefinitionHash { get; init; }
    public string? ExtensionOrigin { get; init; }
    public string? CorrelationId { get; init; }
    public required DateTimeOffset AppliedAt { get; init; }
}

public sealed record KnowledgeSourceProfileProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public string Version { get; init; } = "1.0.0";
    public bool Publish { get; init; }
    public bool Activate { get; init; } = true;
    public JsonElement ConfigurationSchema { get; init; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        additionalProperties = true
    });
    public required KnowledgeFlowTarget IngestionFlow { get; init; }
    public required KnowledgeFlowTarget RetrievalFlow { get; init; }
    public IReadOnlyList<KnowledgeSourceProfileStorageFlow> StorageFlows { get; init; } = [];
    public IReadOnlyList<KnowledgeSourceProfileToolBinding> ToolBindings { get; init; } = [];
    public IReadOnlyList<KnowledgeSourceProfileToolSetRoute> ToolSetRoutes { get; init; } = [];
    public IReadOnlyDictionary<string, JsonElement> Limits { get; init; } = new Dictionary<string, JsonElement>();
    public IReadOnlyDictionary<string, JsonElement> Policies { get; init; } = new Dictionary<string, JsonElement>();
    public KnowledgeSourceProfileApplicationProvenance? AppliedFrom { get; init; }
}

public sealed record KnowledgeSourceProfileResource : Resource
{
    public KnowledgeSourceProfileProperties Definition { get; init; } = null!;
    public string? ActiveVersion { get; init; }
}

public sealed record PublishedKnowledgeSourceProfileToolBinding
{
    public required string Name { get; init; }
    public required string Capability { get; init; }
    public required string ToolName { get; init; }
    public required ResourceNamespace ToolNamespace { get; init; }
    public required Guid ToolUid { get; init; }
    public required long ToolGeneration { get; init; }
    public required string ProviderName { get; init; }
    public required ResourceNamespace ProviderNamespace { get; init; }
    public required Guid ProviderUid { get; init; }
    public required long ProviderGeneration { get; init; }
    public required string ExternalToolId { get; init; }
}

public sealed record PublishedKnowledgeSourceProfileToolSetRoute
{
    public required string Name { get; init; }
    public required string ToolSetName { get; init; }
    public required ResourceNamespace ToolSetNamespace { get; init; }
    public required Guid ToolSetUid { get; init; }
    public required string Version { get; init; }
    public required string DefinitionHash { get; init; }
    public required string Capability { get; init; }
    public string? Route { get; init; }
    public required PublishedKnowledgeSourceProfileToolBinding SelectedTool { get; init; }
}

public sealed record ResolvedKnowledgeSourceProfile
{
    public required string Name { get; init; }
    public required ResourceNamespace Namespace { get; init; }
    public required Guid Uid { get; init; }
    public required long Generation { get; init; }
    public required string Version { get; init; }
    public required string DefinitionHash { get; init; }
    public required JsonElement ConfigurationSchema { get; init; }
    public required ResolvedKnowledgeFlowBinding IngestionFlow { get; init; }
    public required ResolvedKnowledgeFlowBinding RetrievalFlow { get; init; }
    public IReadOnlyList<ResolvedKnowledgeFlowBinding> StorageFlows { get; init; } = [];
    public IReadOnlyList<PublishedKnowledgeSourceProfileToolBinding> ToolBindings { get; init; } = [];
    public IReadOnlyList<PublishedKnowledgeSourceProfileToolSetRoute> ToolSetRoutes { get; init; } = [];
    public IReadOnlyDictionary<string, JsonElement> Limits { get; init; } = new Dictionary<string, JsonElement>();
    public IReadOnlyDictionary<string, JsonElement> Policies { get; init; } = new Dictionary<string, JsonElement>();
    public KnowledgeSourceProfileApplicationProvenance? AppliedFrom { get; init; }
}

public sealed record KnowledgeSourceProfileRevisionResource : Resource, IImmutableResource
{
    public required Guid ProfileUid { get; init; }
    public required string ProfileName { get; init; }
    public required long ProfileGeneration { get; init; }
    public required string Version { get; init; }
    public required string DefinitionHash { get; init; }
    public required DateTimeOffset PublishedAt { get; init; }
    public required Guid PublishedBy { get; init; }
    public required KnowledgeSourceProfileProperties Definition { get; init; }
    public required ResolvedKnowledgeSourceProfile Resolution { get; init; }
}

public sealed record CreateKnowledgeSourceProfileRequest(
    string Name,
    KnowledgeSourceProfileProperties Properties,
    string? Namespace = null);

public sealed record PutKnowledgeSourceProfileRequest(KnowledgeSourceProfileProperties Properties);

public sealed record PublishKnowledgeSourceProfileRequest
{
    public required string Version { get; init; }
    public bool Activate { get; init; } = true;
    public JsonElement ConfigurationDefaults { get; init; } = JsonSerializer.SerializeToElement(new { });
}

public sealed record ActivateKnowledgeSourceProfileRequest
{
    public required string Version { get; init; }
    public JsonElement ConfigurationDefaults { get; init; } = JsonSerializer.SerializeToElement(new { });
}

public sealed record PreviewKnowledgeSourceProfileApplicationRequest
{
    public required ResourceReference SourceProfile { get; init; }
    public required string SourceVersion { get; init; }
    public required string TargetVersion { get; init; }
    public JsonElement ConfigurationDefaults { get; init; } = JsonSerializer.SerializeToElement(new { });
    public string? CorrelationId { get; init; }
}

public sealed record KnowledgeSourceProfileSourceImpact
{
    public required string SourceName { get; init; }
    public required ResourceNamespace SourceNamespace { get; init; }
    public required bool Compatible { get; init; }
    public IReadOnlyList<string> Issues { get; init; } = [];
    public IReadOnlyList<string> AppliedDefaults { get; init; } = [];
}

public sealed record KnowledgeSourceProfileDependencyChange
{
    public required string Role { get; init; }
    public string? Previous { get; init; }
    public string? Proposed { get; init; }
    public required bool Available { get; init; }
}

public sealed record KnowledgeSourceProfileApplicationPlan
{
    public required string TargetProfileName { get; init; }
    public required ResourceNamespace TargetProfileNamespace { get; init; }
    public required string SourceProfileName { get; init; }
    public required ResourceNamespace SourceProfileNamespace { get; init; }
    public required string SourceVersion { get; init; }
    public required string TargetVersion { get; init; }
    public required string SourceDefinitionHash { get; init; }
    public required bool Ready { get; init; }
    public IReadOnlyList<KnowledgeSourceProfileDependencyChange> DependencyChanges { get; init; } = [];
    public IReadOnlyList<KnowledgeSourceProfileSourceImpact> Sources { get; init; } = [];
    public IReadOnlyList<string> Issues { get; init; } = [];
}
