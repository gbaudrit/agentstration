using System.Text.Json;
using Agentstration.Asp.Abstractions;
using Agentstration.Resources;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;

namespace Agentstration.Infrastructure.Storage;

public sealed class LocalStorageDefinitionValidator : IStorageDefinitionValidator
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public void Validate(StorageTypeResource type, StorageTypeVersionDescriptor version, StorageProperties definition)
    {
        if (type.Governance.ManagedBy?.Kind != ResourceManagementAuthorityKind.Core
            || type.Governance.ManagedBy.Reference != "storage.local"
            || version.Version != StoragePlatformResourceProvisioner.ContentTypeVersion) return;
        try
        {
            var policy = definition.Envelope.Deserialize<StorageContentPolicy>(Json)
                ?? throw new AspException("invalid_policy", "A local envelope is required.");
            AspValidation.Policy(new(policy.List, policy.Stat, policy.Read, policy.Write, policy.Prefix,
                policy.MaximumReadBytes, policy.MaximumWriteBytes, policy.MaximumPageSize, policy.KeyFrom, policy.KeyBefore));
        }
        catch (Exception exception) when (exception is AspException or JsonException)
        { throw new StorageException("storage_configuration_invalid", "The local Storage envelope contains invalid restrictions."); }
    }
}
