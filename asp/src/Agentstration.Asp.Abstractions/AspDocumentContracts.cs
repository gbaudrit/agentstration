using System.Text.Json;

namespace Agentstration.Asp.Abstractions;

/// <summary>Independent from asp.content/1; language profiles do not change document identity.</summary>
public static class AspDocuments
{
    public const string Profile = "asp.documents/1";
    public const string PublicationContract = "asp.document-publication/1";
    public const int MaximumDocumentBytes = 65_536;
    public const int MaximumResultBytes = 196_608;
}

public sealed record AspQueryProfileReference(string Id, string Version, string Encoding, string ModelDigest);
public sealed record AspQueryProfileDescriptor(AspQueryProfileReference Reference, string Specification,
    IReadOnlyList<string> Features, JsonElement InputSchema, int MaximumNodes = 64);
public sealed record AspDocumentSelection(string Collection, string? Publication = null);
public sealed record AspDocumentCollectionDeclaration(string Id, JsonElement Schema, AspDocumentPolicy Policy);
public sealed record AspDocumentInvocation<T>(AspStoreRef Store, AspDocumentSelection Selection, T Arguments);

/// <summary>A host-derived restriction, never an authorization decision supplied in business arguments.</summary>
public sealed record AspDocumentPolicy(string Collection, bool Get = true, bool Put = false,
    bool Delete = false, bool Query = true, bool Publish = false, int MaximumPageSize = 50,
    int MaximumDocumentBytes = AspDocuments.MaximumDocumentBytes,
    int MaximumResultBytes = AspDocuments.MaximumResultBytes,
    IReadOnlyList<string>? QueryProfiles = null);
public sealed record AspDocumentContext(AspStoreRef Store, AspDocumentSelection Selection,
    string Subject, string AuthorizationRevision, AspDocumentPolicy Policy);
public sealed record AspDocumentDescription(string AspVersion, string Profile, AspStoreRef Store,
    AspDocumentSelection Selection, string ProviderId, string ProviderVersion, AspDocumentPolicy Policy,
    IReadOnlyList<AspQueryProfileDescriptor> QueryProfiles, JsonElement DocumentSchema,
    string SchemaDigest, bool ImmutablePublications, JsonElement ConfigurationSchema);
public sealed record AspDocument(string Id, string Revision, JsonElement Value);
public sealed record AspDocumentInput(string Id, JsonElement Value);
public sealed record AspDocumentGet(string RequestId, string Id, string? Revision = null);
/// <summary>Missing expected revision creates only. Replacement requires exact current revision.</summary>
public sealed record AspDocumentPut(string RequestId, DateTimeOffset NotAfter, string Id,
    JsonElement Value, string? ExpectedRevision = null);
public sealed record AspDocumentDelete(string RequestId, DateTimeOffset NotAfter, string Id, string ExpectedRevision);
public sealed record AspDocumentDeleted(string Id, string Revision);
public sealed record AspDocumentQuery(string RequestId, AspQueryProfileReference Profile,
    JsonElement Query, int PageSize = 50, string? Cursor = null);
public sealed record AspDocumentPage(IReadOnlyList<AspDocument> Documents,
    IReadOnlyList<JsonElement> Groups, string? Cursor = null);
/// <summary>Provider position is protected by the server, not returned as caller-editable SQL/offset.</summary>
public sealed record AspDocumentProviderPage(IReadOnlyList<AspDocument> Documents,
    IReadOnlyList<JsonElement> Groups, string? Position = null);
public sealed record AspDocumentPrepare(string RequestId, DateTimeOffset NotAfter,
    string PublicationId, IReadOnlyList<AspDocumentInput> Documents);
public sealed record AspDocumentPublication(string Id, string Digest, int Count, string SchemaDigest,
    string Contract = AspDocuments.PublicationContract);
public sealed record AspDocumentPublicationRequest(string RequestId, string PublicationId, string Digest);

/// <summary>Providers enforce trusted target, restrictions and publication immutability on every call.</summary>
public interface IAspDocumentProvider
{
    string ProviderId { get; }
    string ProviderVersion { get; }
    JsonElement ConfigurationSchema { get; }
    JsonElement DocumentSchema { get; }
    IReadOnlyList<AspQueryProfileDescriptor> QueryProfiles { get; }
    ValueTask<AspDocument> GetAsync(AspDocumentContext context, AspDocumentGet request, CancellationToken ct);
    ValueTask<AspDocument> PutAsync(AspDocumentContext context, AspDocumentPut request, CancellationToken ct);
    ValueTask<AspDocumentDeleted> DeleteAsync(AspDocumentContext context, AspDocumentDelete request, CancellationToken ct);
    ValueTask<AspDocumentProviderPage> QueryAsync(AspDocumentContext context, AspDocumentQuery request,
        string? position, CancellationToken ct);
}

public interface IAspDocumentPublisher
{
    ValueTask<AspDocumentPublication> PrepareAsync(AspDocumentContext context, AspDocumentPrepare request, CancellationToken ct);
    ValueTask<AspDocumentPublication> SealAsync(AspDocumentContext context, AspDocumentPublicationRequest request, CancellationToken ct);
    ValueTask<AspDocumentPublication> InspectAsync(AspDocumentContext context, AspDocumentPublicationRequest request, CancellationToken ct);
    ValueTask RetireAsync(AspDocumentContext context, AspDocumentPublicationRequest request, CancellationToken ct);
}
