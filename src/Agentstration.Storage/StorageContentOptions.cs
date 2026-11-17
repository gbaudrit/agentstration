using Agentstration.Storage.Contracts;

namespace Agentstration.Storage;

/// <summary>Operator ceilings; resource envelopes and consumer rules can only reduce them.</summary>
public sealed record StorageContentOptions
{
    public const string SectionName = "Agentstration:StorageContent";
    public const int SupportedMaximumBytes = 16 * 1024 * 1024;
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
        if (MaximumReadBytes is < 1 or > SupportedMaximumBytes
            || MaximumWriteBytes is < 1 or > SupportedMaximumBytes
            || MaximumObjectBytes is < 1 or > SupportedMaximumBytes
            || TransferChunkBytes is < 1 or > 65536 || MaximumPageSize is < 1 or > 100
            || LocalMaximumStoreBytes < MaximumObjectBytes || LocalMaximumStoreBytes > 1024L * 1024 * 1024
            || LocalMaximumObjects is < 1 or > 100000 || LocalMaximumReceipts is < 1 or > 100000
            || LocalMaximumPublications is < 1 or > 100000)
            throw new InvalidOperationException($"{SectionName} contains invalid content limits or local capacities.");
    }

    public StorageContentPolicy DefaultPolicy(bool write = false) => new(Write: write,
        MaximumPageSize: MaximumPageSize, MaximumReadBytes: MaximumReadBytes, MaximumWriteBytes: MaximumWriteBytes);

    public static void ValidatePolicy(StorageContentPolicy policy)
    {
        if (policy.MaximumReadBytes is < 1 or > SupportedMaximumBytes
            || policy.MaximumWriteBytes is < 1 or > SupportedMaximumBytes
            || policy.MaximumPageSize is < 1 or > 100)
            throw new StorageException("storage_content_invalid_policy", "Content policy limits are outside the supported bounds.");
    }
}
