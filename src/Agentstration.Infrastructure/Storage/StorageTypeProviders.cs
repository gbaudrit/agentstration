using System.Text.Json;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;

namespace Agentstration.Infrastructure.Storage;

/// <summary>Local metadata only; ASP/AEP adapters own runtime provider observations in #1040.</summary>
public sealed class StorageTypeAvailabilityProvider(IResourceStore store, IRequestContextScopeFactory requestScopes) : IStorageTypeAvailabilityProvider
{
    public async Task<bool> IsAvailableAsync(StorageTypeResource type, StorageTypeVersionDescriptor version,
        CancellationToken cancellationToken)
    {
        if (version.Contribution is not { } proof) return true;
        using var system = requestScopes.PushSystem();
        var registration = await store.GetExactAsync<ExtensionRegistrationResource>(proof.Provider, cancellationToken);
        return registration?.Value.Uid == proof.ProviderUid && registration.Value.Generation == proof.ProviderGeneration
            && registration.Value.Definition.Enabled
            && (registration.Value.Definition.ExpectedExtensionId is null
                || registration.Value.Definition.ExpectedExtensionId == type.Governance.Origin.Reference);
    }
}

public sealed class StoragePlatformResourceProvisioner(StorageTypeManagementService types, IResourceGovernanceContext governance)
{
    public const string LocalTypeName = "local";
    public const string LocalTypeVersion = "1.0.0";

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        using var authority = governance.Push(ResourceGovernance.Core("storage.local", LocalTypeVersion));
        var existing = await types.GetExactAsync(ResourceScopeRef.Instance, ResourceNamespace.Default, LocalTypeName, cancellationToken);
        var schema = JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false });
        _ = await types.SynchronizeManagedAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = StorageResourceKinds.StorageType,
            Metadata = new()
            {
                Name = LocalTypeName,
                Annotations = existing?.Value.Metadata.Annotations
                    ?? new Dictionary<string, string> { [ResourceProvenanceAnnotations.BuiltIn] = "true" }
            },
            ScopeRef = ResourceScopeRef.Instance,
            Definition = new()
            {
                DisplayName = "Local storage",
                Description = "A local logical Storage without a remote connection, with bounded filesystem content operations.",
                Enabled = existing?.Value.Definition.Enabled ?? true,
                UsePolicy = existing?.Value.Definition.UsePolicy ?? new(),
                PreferredVersion = LocalTypeVersion,
                Versions =
                [
                    new()
                    {
                        Version = LocalTypeVersion, SchemaDigest = StorageDigests.Schema(schema), ConfigurationSchema = schema,
                        EnvelopeSchemaDigest = StorageDigests.Schema(LocalEnvelopeSchema), EnvelopeSchema = LocalEnvelopeSchema,
                        Capabilities = new() { List = true, Stat = true, Read = true, Write = true },
                        Limits = new(100, 65536, 65536)
                    }
                ]
            }
        }, cancellationToken);
    }

    public static JsonElement LocalEnvelopeSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        additionalProperties = false,
        properties = new Dictionary<string, object>
        {
            ["list"] = new { type = "boolean" },
            ["stat"] = new { type = "boolean" },
            ["read"] = new { type = "boolean" },
            ["write"] = new { type = "boolean" },
            ["prefix"] = new { type = "string", maxLength = 256 },
            ["maximumPageSize"] = new { type = "integer", minimum = 1, maximum = 100 },
            ["maximumReadBytes"] = new { type = "integer", minimum = 1, maximum = 65536 },
            ["maximumWriteBytes"] = new { type = "integer", minimum = 1, maximum = 65536 },
            ["keyFrom"] = new { type = "string", maxLength = 256 },
            ["keyBefore"] = new { type = "string", maxLength = 256 }
        }
    });
}
