namespace Agentstration.Storage.Contracts;

/// <summary>A consumer can only narrow the Storage envelope; it cannot replace a connection or implementation.</summary>
public sealed record StorageContentPolicy(
    bool List = true, bool Stat = true, bool Read = true, bool Write = false,
    string Prefix = "", int MaximumPageSize = 100, int MaximumReadBytes = 65536,
    int MaximumWriteBytes = 65536, string? KeyFrom = null, string? KeyBefore = null);

public sealed record StorageContentRequest(
    StorageRevisionReference Storage, StorageResolutionContext Consumer, StorageContentPolicy Policy);

public sealed record StorageObject(string Key, string? Version, long Length, string? Sha256, string Reference);
public sealed record StorageObjectPage(IReadOnlyList<StorageObject> Objects, string? Cursor);
public sealed record StorageObjectContent(StorageObject Item, long Offset, string ContentBase64, bool EndOfObject);
public sealed record StorageWrite(string RequestId, DateTimeOffset NotAfter, string Key,
    string ContentBase64, string? ExpectedVersion = null);

/// <summary>Each operation reauthorizes an exact Storage. No session is an authorization cache.</summary>
public interface IStorageContentGateway
{
    Task ValidateAsync(StorageContentRequest target, CancellationToken cancellationToken = default);
    Task<StorageObjectPage> ListAsync(StorageContentRequest target, string prefix, int limit,
        string? cursor = null, CancellationToken cancellationToken = default);
    Task<StorageObject> StatAsync(StorageContentRequest target, string reference,
        CancellationToken cancellationToken = default);
    Task<StorageObjectContent> ReadAsync(StorageContentRequest target, string reference, long offset, int maximumBytes,
        CancellationToken cancellationToken = default);
    Task<StorageObject> WriteAsync(StorageContentRequest target, StorageWrite request,
        CancellationToken cancellationToken = default);
}

/// <summary>Versioned protocol mapping; contains no credentials or physical target configuration.</summary>
public sealed record StorageAspBindingDescriptor(string Profile, string McpServer, string ToolPrefix,
    string ProviderId, string ProviderVersion, string BindingPath);

/// <summary>Pure provider-owned validation in addition to the immutable JSON schemas. Never resolves values or performs I/O.</summary>
public interface IStorageDefinitionValidator
{
    void Validate(StorageTypeResource type, StorageTypeVersionDescriptor version, StorageProperties definition);
}
