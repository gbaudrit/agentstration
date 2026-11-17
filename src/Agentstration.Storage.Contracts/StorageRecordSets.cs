using System.Security.Cryptography;
using System.Text.Json;

namespace Agentstration.Storage.Contracts;

/// <summary>Compact immutable membership evidence for a logical collection of JSON objects.</summary>
public static class StorageRecordSets
{
    public const int MaximumRecords = 100;
    public static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static byte[] Encode(IReadOnlyList<JsonElement> records)
    {
        if (records.Count is < 1 or > MaximumRecords || records.Any(value => value.ValueKind != JsonValueKind.Object))
            throw new ArgumentException("A record set requires one to one hundred JSON objects.", nameof(records));
        // Admission budgets belong to the acquisition/publication services, not this encoding contract.
        return StorageDocumentEncoding.Encode(JsonSerializer.SerializeToElement(records));
    }

    public static IReadOnlyList<StoragePublicationReference> Members(StoragePublicationReference reference)
    {
        if (reference.Records is not { } records) return [reference];
        if (reference.Document is not null || records.Members is null || records.Members.Count is < 1 or > MaximumRecords
            || records.Members.Any(member => member is null || member.Id is null || member.Id.Length is < 1 or > 80
                || member.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')
                || member.Length < 2 || member.Sha256 is null || member.Sha256.Length != 64 || !member.Sha256.All(char.IsAsciiHexDigit))
            || records.Members.Select(member => member.Id).Distinct(StringComparer.Ordinal).Count() != records.Members.Count
            || reference.Length != records.Members.Sum(member => (decimal)member.Length) + records.Members.Count + 1
            || reference.Sha256 is null || reference.Sha256.Length != 64 || !reference.Sha256.All(char.IsAsciiHexDigit)
            || reference.Key is null)
            throw new ArgumentException("Invalid JSON record membership evidence.", nameof(reference));
        var publication = records.DocumentPublication;
        if (publication is not null && (publication.Target.Storage != reference.Target.Storage
            || publication.Target.Consumer != reference.Target.Consumer
            || publication.Target.CollectionUid != reference.Target.DocumentCollectionUid
            || publication.Publication.Id != publication.Target.Publication
            || publication.Publication.Count < records.Members.Count
            || reference.Version != publication.Publication.Digest))
            throw new ArgumentException("Invalid document record publication evidence.", nameof(reference));
        if (publication is null && reference.Version != reference.Sha256)
            throw new ArgumentException("Invalid file record collection version.", nameof(reference));
        var prefix = reference.Key[..(reference.Key.LastIndexOf('/') + 1)];
        return records.Members.Select(member => new StoragePublicationReference(reference.Target,
            publication is null ? prefix + member.Id : member.Id,
            publication?.Publication.Digest ?? member.Sha256, member.Length, member.Sha256)
        {
            Binding = reference.Binding,
            Document = publication is null ? null : new(publication, member.Id, publication.Publication.Digest)
        }).ToArray();
    }

    public static StorageDocumentPublicationReference? DocumentPublication(StoragePublicationReference reference) =>
        reference.Records?.DocumentPublication ?? reference.Document?.Publication;
}
