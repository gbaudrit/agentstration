using Agentstration.Resources;

namespace Agentstration.Storage.Contracts;

public sealed record StorageDocumentToolPublication(string Version, IReadOnlyList<string> Operations,
    StorageQueryProfilePin? QueryProfile = null, bool RequiresApproval = false);

public interface IStorageDocumentToolExposure
{
    Task<IReadOnlyList<ResourceReference>> PublishAsync(StorageDocumentTarget target,
        StorageDocumentToolPublication request, CancellationToken ct);
}
