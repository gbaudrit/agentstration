using Agentstration.Storage.Contracts;

namespace Agentstration.Storage;

/// <summary>Operator ceilings; resource envelopes and consumer rules can only reduce them.</summary>
public sealed record StorageContentOptions
{
    public const string SectionName = "Agentstration:StorageContent";
    public int MaximumReadBytes { get; init; } = 10 * 1024 * 1024;
    public int MaximumWriteBytes { get; init; } = 10 * 1024 * 1024;
    public int MaximumObjectBytes { get; init; } = 10 * 1024 * 1024;
    public int TransferChunkBytes { get; init; } = 65536;
    public int MaximumPageSize { get; init; } = 100;
    public long LocalMaximumStoreBytes { get; init; } = 64 * 1024 * 1024;
    public int LocalMaximumObjects { get; init; } = 1024;
    public int LocalMaximumReceipts { get; init; } = 512;
    public int LocalMaximumPublications { get; init; } = 1024;

    public void Validate()
    {
        if (MaximumReadBytes < 1 || MaximumWriteBytes < 1 || MaximumObjectBytes < 1
            || TransferChunkBytes < 1 || MaximumPageSize < 1
            || LocalMaximumStoreBytes < MaximumObjectBytes
            || LocalMaximumObjects < 1 || LocalMaximumReceipts < 1 || LocalMaximumPublications < 1)
            throw new InvalidOperationException($"{SectionName} contains invalid content limits or local capacities.");
    }

    public StorageContentPolicy DefaultPolicy(bool write = false) => new(Write: write,
        MaximumPageSize: MaximumPageSize, MaximumReadBytes: MaximumReadBytes, MaximumWriteBytes: MaximumWriteBytes);

    public static void ValidatePolicy(StorageContentPolicy policy)
    {
        if (policy.MaximumReadBytes < 1 || policy.MaximumWriteBytes < 1 || policy.MaximumPageSize < 1)
            throw new StorageException("storage_content_invalid_policy", "Content policy limits must be positive.");
    }
}
