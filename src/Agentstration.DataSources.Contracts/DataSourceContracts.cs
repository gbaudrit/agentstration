using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.DataSources.Contracts;

public static class DataSourceResourceKinds
{
    public const string DataSource = "DataSource";
    public const string DataSourceProfile = "DataSourceProfile";
    public const string DataSourceProfileRevision = "DataSourceProfileRevision";
    public const string DataSourceAcquisition = "DataSourceAcquisition";
}

public static class DataSourceFlowContracts
{
    public const string MetadataKey = "dataSource.contract";
    public const string Acquisition = "data.source.acquisition/v1";
}

public sealed record DataSourceFlowTarget
{
    public required string Name { get; init; }
    public ResourceNamespace? Namespace { get; init; }
    public string? Version { get; init; }
    public bool UseActiveVersion { get; init; } = true;
}

public sealed record DataSourceProfileToolBinding
{
    public required string Name { get; init; }
    public required string Capability { get; init; }
    public required ResourceReference Tool { get; init; }
    public ResourceReference? RequiredProvider { get; init; }
}

public sealed record DataSourceProfileProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public string Version { get; init; } = "1.0.0";
    public JsonElement ConfigurationSchema { get; init; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        additionalProperties = true
    });
    public required DataSourceFlowTarget AcquisitionFlow { get; init; }
    public IReadOnlyList<DataSourceProfileToolBinding> ToolBindings { get; init; } = [];
    public IReadOnlyDictionary<string, JsonElement> Limits { get; init; } = new Dictionary<string, JsonElement>();
    public IReadOnlyDictionary<string, JsonElement> Policies { get; init; } = new Dictionary<string, JsonElement>();
    public IReadOnlyDictionary<string, JsonElement> CompatibilityRequirements { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record DataSourceProfileResource : Resource
{
    public DataSourceProfileProperties Definition { get; init; } = null!;
    public string? ActiveVersion { get; init; }
}

public sealed record DataSourceProfileRevisionResource : Resource, IImmutableResource
{
    public required Guid ProfileUid { get; init; }
    public required string ProfileName { get; init; }
    public required long ProfileGeneration { get; init; }
    public required string Version { get; init; }
    public required string DefinitionHash { get; init; }
    public required DateTimeOffset PublishedAt { get; init; }
    public required Guid PublishedBy { get; init; }
    public required DataSourceProfileProperties Definition { get; init; }
}

public sealed record DataSourceProperties
{
    public required string DisplayName { get; init; }
    public string? Description { get; init; }
    public bool Enabled { get; init; } = true;
    public required ResourceReference Profile { get; init; }
    public JsonElement Configuration { get; init; } = JsonSerializer.SerializeToElement(new { });
}

public sealed record DataSourceMigrationInput
{
    public required string SourceKind { get; init; }
    public required ResourceScopeRef ScopeRef { get; init; }
    public required ResourceNamespace Namespace { get; init; }
    public required string Name { get; init; }
    public required Guid Uid { get; init; }
    public required long Generation { get; init; }
}

public sealed record DataSourceResource : Resource
{
    public DataSourceProperties Definition { get; init; } = null!;
    public DataSourceMigrationInput? MigratedFrom { get; init; }
}

public sealed record ResolvedDataSourceProfile
{
    public required string Name { get; init; }
    public required ResourceNamespace Namespace { get; init; }
    public required ResourceScopeRef ScopeRef { get; init; }
    public required Guid Uid { get; init; }
    public required long Generation { get; init; }
    public required string Version { get; init; }
    public required string DefinitionHash { get; init; }
    public required DataSourceProfileProperties Definition { get; init; }
}

public sealed record ResolvedDataSourceFlowBinding(
    string Name,
    ResourceNamespace Namespace,
    string Version,
    bool UsesActiveVersion,
    JsonElement? InputSchema,
    JsonElement? OutputSchema,
    string? Contract);

public sealed record ResolvedDataSourceToolBinding
{
    public required string BindingName { get; init; }
    public required string Capability { get; init; }
    public required ResourceScopeRef ToolScopeRef { get; init; }
    public required string ToolName { get; init; }
    public required ResourceNamespace ToolNamespace { get; init; }
    public required Guid ToolUid { get; init; }
    public required long ToolGeneration { get; init; }
    public required ResourceScopeRef ProviderScopeRef { get; init; }
    public required string ProviderName { get; init; }
    public required ResourceNamespace ProviderNamespace { get; init; }
    public required Guid ProviderUid { get; init; }
    public required long ProviderGeneration { get; init; }
    public required string ExternalToolId { get; init; }
}

public sealed record ResolvedDataSourceAcquisitionComposition
{
    public required ResolvedDataSourceProfile Profile { get; init; }
    public required ResolvedDataSourceFlowBinding Flow { get; init; }
    public IReadOnlyList<ResolvedDataSourceToolBinding> Tools { get; init; } = [];
    public IReadOnlyDictionary<string, JsonElement> Limits { get; init; } = new Dictionary<string, JsonElement>();
    public IReadOnlyDictionary<string, JsonElement> Policies { get; init; } = new Dictionary<string, JsonElement>();
    public IReadOnlyDictionary<string, JsonElement> CompatibilityRequirements { get; init; } = new Dictionary<string, JsonElement>();
}

public sealed record DataSourceReadiness(
    bool Ready,
    bool Enabled,
    ResolvedDataSourceProfile? Profile,
    IReadOnlyList<string> Issues);

public sealed record CreateDataSourceProfileRequest(
    string Name,
    DataSourceProfileProperties Properties,
    string? Namespace = null,
    ResourceScopeRef? ScopeRef = null);

public sealed record PutDataSourceProfileRequest(DataSourceProfileProperties Properties);
public sealed record PublishDataSourceProfileRequest(string Version, bool Activate = true);
public sealed record ActivateDataSourceProfileRequest(string Version);

public sealed record CreateDataSourceRequest(
    string Name,
    DataSourceProperties Properties,
    string? Namespace = null,
    ResourceScopeRef? ScopeRef = null,
    DataSourceMigrationInput? MigratedFrom = null);

public sealed record PutDataSourceRequest(DataSourceProperties Properties);
public sealed record SetDataSourceEnabledRequest(bool Enabled);

public enum DataSourceAcquisitionState
{
    Pending,
    Running,
    WaitingForInput,
    WaitingForChild,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut
}

public enum DataSourceArtifactDisposition { Intermediate, Diagnostic, Publishable }
public enum DataSourceArtifactKind { Staged, Durable }

public sealed record DataSourceAcquisitionArtifact
{
    public required string ArtifactId { get; init; }
    public required DataSourceArtifactKind Kind { get; init; }
    public required DataSourceArtifactDisposition Disposition { get; init; }
    public string? Name { get; init; }
    public string? MediaType { get; init; }
    public string? Digest { get; init; }
}

public sealed record DataSourceAcquisitionManifest
{
    public IReadOnlyList<DataSourceAcquisitionArtifact> Artifacts { get; init; } = [];
}

public sealed record DataSourceAcquisitionCaller(
    [property: JsonPropertyName("principalId")] Guid PrincipalId,
    [property: JsonPropertyName("tenantId")] Guid TenantId,
    [property: JsonPropertyName("workspaceId")] Guid WorkspaceId);

public sealed record DataSourceAcquisitionInput
{
    [JsonPropertyName("dataSourceId")]
    public required string DataSourceId { get; init; }
    [JsonPropertyName("dataSourceUid")]
    public required Guid DataSourceUid { get; init; }
    [JsonPropertyName("dataSourceGeneration")]
    public required long DataSourceGeneration { get; init; }
    [JsonPropertyName("profile")]
    public required ResolvedDataSourceProfile Profile { get; init; }
    [JsonPropertyName("sourceConfiguration")]
    public JsonElement SourceConfiguration { get; init; } = JsonSerializer.SerializeToElement(new { });
    [JsonPropertyName("parameters")]
    public JsonElement Parameters { get; init; } = JsonSerializer.SerializeToElement(new { });
    [JsonPropertyName("caller")]
    public required DataSourceAcquisitionCaller Caller { get; init; }
    [JsonPropertyName("correlationId")]
    public required string CorrelationId { get; init; }
    [JsonPropertyName("acquisitionId")]
    public required string AcquisitionId { get; init; }
}

public sealed record DataSourceAcquisitionResource : Resource
{
    public required ResourceScopeRef DataSourceScopeRef { get; init; }
    public required Guid DataSourceUid { get; init; }
    public required string DataSourceName { get; init; }
    public required ResourceNamespace DataSourceNamespace { get; init; }
    public required long DataSourceGeneration { get; init; }
    public required ResolvedDataSourceAcquisitionComposition Composition { get; init; }
    public required string FlowRunId { get; init; }
    public required DataSourceAcquisitionState State { get; init; }
    public required string CorrelationId { get; init; }
    public string? IdempotencyKey { get; init; }
    public required string RequestHash { get; init; }
    public JsonElement SourceConfiguration { get; init; } = JsonSerializer.SerializeToElement(new { });
    public JsonElement Parameters { get; init; } = JsonSerializer.SerializeToElement(new { });
    public required Guid CreatedBy { get; init; }
    public required Guid TenantId { get; init; }
    public required Guid WorkspaceId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public int Attempt { get; init; } = 1;
    public string? RetriedFrom { get; init; }
    public DataSourceAcquisitionManifest? Manifest { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record StartDataSourceAcquisitionRequest
{
    public JsonElement Parameters { get; init; } = JsonSerializer.SerializeToElement(new { });
    public string? CorrelationId { get; init; }
    public ResourceScopeRef? DataSourceScopeRef { get; init; }
}

public sealed record RetryDataSourceAcquisitionRequest(string? CorrelationId = null);
