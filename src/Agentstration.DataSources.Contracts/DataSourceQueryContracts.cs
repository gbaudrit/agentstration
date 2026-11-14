using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;

namespace Agentstration.DataSources.Contracts;

public sealed record DataSourceQueryProperties
{
    public required string DisplayName { get; init; }
    public bool Enabled { get; init; } = true;
    public required DataSourceDocumentSelection Selection { get; init; }
    public required JsonElement Query { get; init; }
}

public sealed record DataSourceQueryResource : Resource
{
    public required DataSourceQueryProperties Definition { get; init; }
}

public sealed record DataSourceQueryVersionResource : Resource, IImmutableResource
{
    public required Guid QueryUid { get; init; }
    public required string QueryName { get; init; }
    public required long QueryGeneration { get; init; }
    public required string Version { get; init; }
    public required DataSourceQueryProperties Definition { get; init; }
    public required string ContractDigest { get; init; }
    public required string DefinitionDigest { get; init; }
    public required DateTimeOffset PublishedAt { get; init; }
}

public sealed record DataSourceQueryReference
{
    public required ResourceReference Query { get; init; }
    public required Guid QueryUid { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DataSourceQueryExecutionRequest
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? PageSize { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AcquisitionId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Cursor { get; init; }
}

public sealed record DataSourceQueryExecutionResult(DataSourceQueryReference Query, Guid QueryVersionUid,
    string DefinitionDigest, string ContractDigest, DataSourceDocumentQueryResult Result, string? NextCursor);
public sealed record DataSourceQueryAcquisitionPin(string Id, Guid Uid);
public sealed record PublishDataSourceQueryRequest(string Version);
public sealed record PublishDataSourceQueryToolRequest(string Name, string DisplayName, string Version,
    string? QueryVersion = null, DataSourceQueryAcquisitionPin? Acquisition = null, bool RequiresApproval = false);
public sealed record DataSourceQueryToolPublication(ResourceReference Tool, Guid QueryUid, string QueryName,
    string ToolVersion, string? SelectedQueryVersion, DataSourceQueryAcquisitionPin? Acquisition, bool Legacy = false, bool Generic = false,
    string? DisplayName = null, ResourceReference? Definition = null);
