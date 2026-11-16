using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Storage.Contracts;

namespace Agentstration.Artifacts.Contracts;

/// <summary>Exact physical evidence for produced content, without an expiring access token.</summary>
public sealed record ArtifactStorageMaterialization(WorkspaceId WorkspaceId, StorageContentRequest Target,
    string Key, string? Version, long Length, string Sha256, StoragePublicationReference? Publication = null);

public sealed record RegisterStoredArtifactRequest(string Identity, string ProducerFlowRunId,
    ScopedResourceAddress ProducerResource, string ProducerExecutionId, string MediaType,
    ArtifactStorageMaterialization Materialization, string? ProducerFlowStepId = null,
    Guid ProducerUid = default, long ProducerGeneration = 0, ResolvedDataContract? DataContract = null,
    IReadOnlyDictionary<string, string>? Provenance = null);

public interface IStoredArtifactRegistrar
{
    Task<FlowRunArtifactResource> RegisterStoredAsync(RegisterStoredArtifactRequest request, CancellationToken cancellationToken);
}

public static class ArtifactStorageReferences
{
    private const string Prefix = "artifact-storage-v2.";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string PublicationIdentity(StoragePublicationReference reference) =>
        reference.Document is { } document
            ? "document-publication:" + reference.Target.Storage.StorageUid.ToString("N") + ":" + document.Publication.Target.CollectionUid.ToString("N")
                + ":" + document.Publication.Publication.Id + ":" + document.DocumentId
            : "publication:" + reference.Target.Storage.StorageUid.ToString("N") + ":" + reference.Key;
    public static FlowRunArtifactId Identity(WorkspaceId workspaceId, string identity)
    {
        var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{workspaceId.Value:N}:{identity}"));
        return new(new Guid(digest.AsSpan(0, 16)));
    }
    public static string Encode(ArtifactStorageMaterialization value) =>
        Prefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value, Json));
    public static ArtifactStorageMaterialization? Parse(string value)
    {
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            if (value.Length > 32768) throw new FormatException();
            return JsonSerializer.Deserialize<ArtifactStorageMaterialization>(Convert.FromBase64String(value[Prefix.Length..]), Json)
                ?? throw new FormatException();
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        { throw new ArtifactValidationException("artifact_storage_reference_invalid", "Invalid Artifact materialization reference."); }
    }
}
