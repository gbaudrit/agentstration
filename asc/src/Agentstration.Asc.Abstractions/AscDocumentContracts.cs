using System.Text.Json;

namespace Agentstration.Asc.Abstractions;

/// <summary>Independent from asc.content/1; language profiles do not change document identity.</summary>
public static class AscDocuments
{
    public const string Profile = "asc.documents/1";
    public const string PublicationContract = "asc.document-publication/1";
    public const int MaximumDocumentBytes = 65_536;
    public const int MaximumResultBytes = 196_608;
}

public sealed record AscQueryProfileReference(string Id, string Version, string Encoding, string ModelDigest);
public sealed record AscQueryProfileDescriptor(AscQueryProfileReference Reference, string Specification,
    IReadOnlyList<string> Features, JsonElement InputSchema, int MaximumNodes = 64);
public sealed record AscDocumentSelection(string Collection, string? Publication = null);
public sealed record AscDocumentCollectionDeclaration(string Id, JsonElement Schema, AscDocumentPolicy Policy);
public sealed record AscDocumentInvocation<T>(AscStoreRef Store, AscDocumentSelection Selection, T Arguments);

/// <summary>A host-derived restriction, never an authorization decision supplied in business arguments.</summary>
public sealed record AscDocumentPolicy(string Collection, bool Get = true, bool Put = false,
    bool Delete = false, bool Query = true, bool Publish = false, int MaximumPageSize = 50,
    int MaximumDocumentBytes = AscDocuments.MaximumDocumentBytes,
    int MaximumResultBytes = AscDocuments.MaximumResultBytes,
    IReadOnlyList<string>? QueryProfiles = null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? DocumentIds { get; init; }
}
public sealed record AscDocumentContext(AscStoreRef Store, AscDocumentSelection Selection,
    string Subject, string AuthorizationRevision, AscDocumentPolicy Policy);
public sealed record AscDocumentDescription(string AscVersion, string Profile, AscStoreRef Store,
    AscDocumentSelection Selection, string ProviderId, string ProviderVersion, AscDocumentPolicy Policy,
    IReadOnlyList<AscQueryProfileDescriptor> QueryProfiles, JsonElement DocumentSchema,
    string SchemaDigest, bool ImmutablePublications, JsonElement ConfigurationSchema);
public sealed record AscDocument(string Id, string Revision, JsonElement Value);
public sealed record AscDocumentInput(string Id, JsonElement Value);
public sealed record AscDocumentGet(string RequestId, string Id, string? Revision = null);
/// <summary>Missing expected revision creates only. Replacement requires exact current revision.</summary>
public sealed record AscDocumentPut(string RequestId, DateTimeOffset NotAfter, string Id,
    JsonElement Value, string? ExpectedRevision = null);
public sealed record AscDocumentDelete(string RequestId, DateTimeOffset NotAfter, string Id, string ExpectedRevision);
public sealed record AscDocumentDeleted(string Id, string Revision);
public sealed record AscDocumentQuery(string RequestId, AscQueryProfileReference Profile,
    JsonElement Query, int PageSize = 50, string? Cursor = null);
public sealed record AscDocumentPage(IReadOnlyList<AscDocument> Documents,
    IReadOnlyList<JsonElement> Groups, string? Cursor = null);
/// <summary>Provider position is protected by the server, not returned as caller-editable SQL/offset.</summary>
public sealed record AscDocumentProviderPage(IReadOnlyList<AscDocument> Documents,
    IReadOnlyList<JsonElement> Groups, string? Position = null);
public sealed record AscDocumentPrepare(string RequestId, DateTimeOffset NotAfter,
    string PublicationId, IReadOnlyList<AscDocumentInput> Documents);
public sealed record AscDocumentPublication(string Id, string Digest, int Count, string SchemaDigest,
    string Contract = AscDocuments.PublicationContract);
public sealed record AscDocumentPublicationRequest(string RequestId, string PublicationId, string Digest);

/// <summary>Providers enforce trusted target, restrictions and publication immutability on every call.</summary>
public interface IAscDocumentProvider
{
    string ProviderId { get; }
    string ProviderVersion { get; }
    JsonElement ConfigurationSchema { get; }
    JsonElement DocumentSchema { get; }
    IReadOnlyList<AscQueryProfileDescriptor> QueryProfiles { get; }
    ValueTask<AscDocument> GetAsync(AscDocumentContext context, AscDocumentGet request, CancellationToken ct);
    ValueTask<AscDocument> PutAsync(AscDocumentContext context, AscDocumentPut request, CancellationToken ct);
    ValueTask<AscDocumentDeleted> DeleteAsync(AscDocumentContext context, AscDocumentDelete request, CancellationToken ct);
    ValueTask<AscDocumentProviderPage> QueryAsync(AscDocumentContext context, AscDocumentQuery request,
        string? position, CancellationToken ct);
}

/// <summary>Provider enforces DocumentIds before filtering, aggregation and pagination.</summary>
public interface IAscDocumentSubsetProvider;

public interface IAscDocumentPublisher
{
    ValueTask<AscDocumentPublication> PrepareAsync(AscDocumentContext context, AscDocumentPrepare request, CancellationToken ct);
    ValueTask<AscDocumentPublication> SealAsync(AscDocumentContext context, AscDocumentPublicationRequest request, CancellationToken ct);
    ValueTask<AscDocumentPublication> InspectAsync(AscDocumentContext context, AscDocumentPublicationRequest request, CancellationToken ct);
    ValueTask RetireAsync(AscDocumentContext context, AscDocumentPublicationRequest request, CancellationToken ct);
}
