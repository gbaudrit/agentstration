using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.Resources;
using Agentstration.Schemas.Contracts;
using Agentstration.Storage.Contracts;

namespace Agentstration.DataSources.Contracts;

public static class DataSourceDocumentSelectionModes
{
    public const string Latest = "latest";
    public const string Exact = "exact";
}

public sealed record DataSourceDocumentContractPin(StorageQueryProfilePin Profile,
    SchemaVersionReference? SchemaReference = null, Guid? SchemaUid = null,
    string? DefinitionHash = null, string? ParentDefinitionHash = null);

/// <summary>A source and acquisition stream, never a caller-selected physical Storage target.</summary>
public sealed record DataSourceDocumentSelection
{
    public required ResourceReference Source { get; init; }
    public required Guid SourceUid { get; init; }
    public required ResourceScopeRef AcquisitionScope { get; init; }
    public required DataSourceDocumentContractPin Contract { get; init; }
    public string Mode { get; init; } = DataSourceDocumentSelectionModes.Latest;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AcquisitionId { get; init; }
    public Guid? AcquisitionUid { get; init; }
}

public sealed record DataSourceDocumentDescription(DataSourceDocumentSelection Selection,
    string AcquisitionId, Guid AcquisitionUid, string PublicationId, StorageDocumentDescription Documents);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DataSourceDocumentQueryRequest
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StorageQueryProfilePin? Profile { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Query { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PageSize { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AcquisitionId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Cursor { get; init; }
}

public sealed record DataSourceDocumentQueryResult(string AcquisitionId, Guid AcquisitionUid,
    string PublicationId, DataSourceDocumentContractPin Contract, StorageDocumentPage Page,
    string? NextCursor = null, DateTimeOffset? CursorExpiresAt = null);

public sealed record PublishDataSourceDocumentToolRequest(string Version, StorageQueryProfilePin Profile,
    string Mode = DataSourceDocumentSelectionModes.Latest, bool RequiresApproval = false);

public interface IDataSourceDocumentCursorProtector
{
    string Protect(string value);
    string Unprotect(string value);
}
