using Agentstration.Aep.Abstractions;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Extensions.Aep;
using Agentstration.Extensions.Contracts;
using Agentstration.Resources;
using Agentstration.Storage;
using Agentstration.Storage.Contracts;

namespace Agentstration.Infrastructure.Storage;

public sealed class AepStorageTypeProvisioner(StorageTypeManagementService types,
    IResourceGovernanceContext governance) : IAepEnrollmentResourceProvisioner
{
    public async Task ProvisionAsync(ExtensionRegistrationResource registration, AepManifest? authenticatedManifest,
        CancellationToken cancellationToken)
    {
        if (authenticatedManifest?.Contributions.StorageTypes is not { Count: > 0 } contributions) return;
        if (AepStorageDescriptorValidator.Validate(authenticatedManifest).Count != 0)
            throw new StorageException("storage_contribution_invalid", "The authenticated Storage contribution is invalid.");
        var scope = registration.ScopeRef ?? throw new StorageException("storage_scope_required", "An exact provider scope is required.");
        var provider = new ScopedResourceAddress(scope, registration.Namespace, registration.Kind, registration.Name);
        using var authority = governance.Push(ResourceGovernance.Extension(
            $"{scope.Value}/{registration.Kind}/{registration.Namespace}/{registration.Name}",
            authenticatedManifest.Extension.Id, authenticatedManifest.Extension.Version));
        foreach (var contribution in contributions)
        {
            var ns = ResourceNamespace.Parse(authenticatedManifest.Extension.Id);
            var existing = await types.GetExactAsync(scope, ns, contribution.Id, cancellationToken);
            _ = await types.SynchronizeManagedAsync(new StorageTypeResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = StorageResourceKinds.StorageType,
                ScopeRef = scope,
                Metadata = new() { Name = contribution.Id, Namespace = ns },
                Definition = new()
                {
                    DisplayName = contribution.DisplayName,
                    Description = contribution.Description,
                    Enabled = existing?.Value.Definition.Enabled ?? true,
                    UsePolicy = existing?.Value.Definition.UsePolicy ?? new(),
                    PreferredVersion = contribution.PreferredVersion,
                    Versions = contribution.Versions.Select(version => Map(version, provider,
                        registration.Uid, registration.Generation, contribution.Id)).ToArray()
                }
            }, cancellationToken);
        }
    }

    internal static StorageTypeVersionDescriptor Map(AepStorageTypeVersion version, ScopedResourceAddress provider,
        Guid providerUid, long generation, string contributionId) => new()
        {
            Version = version.Version,
            SchemaDigest = version.SchemaDigest,
            ConfigurationSchema = version.ConfigurationSchema.Clone(),
            EnvelopeSchemaDigest = version.EnvelopeSchemaDigest,
            EnvelopeSchema = version.EnvelopeSchema.Clone(),
            RequiresConnectedService = version.RequiresConnectedService,
            Requirements = version.Requirements.Select(r => new StorageRequirementDescriptor(r.Id, r.Required,
                r.Type switch
                {
                    AepValueType.Text => ConnectedServiceValueType.Text,
                    AepValueType.WholeNumber => ConnectedServiceValueType.WholeNumber,
                    AepValueType.DecimalNumber => ConnectedServiceValueType.DecimalNumber,
                    AepValueType.Logical => ConnectedServiceValueType.Logical,
                    _ => throw new StorageException("storage_requirement_invalid", "The requirement type is unsupported.")
                }, r.Protection == AepValueProtection.Secured ? ConnectedServiceValueProtection.Secured : ConnectedServiceValueProtection.Standard, r.Format)).ToArray(),
            Capabilities = new()
            {
                List = version.Capabilities.List,
                Stat = version.Capabilities.Stat,
                Read = version.Capabilities.Read,
                Write = version.Capabilities.Write,
                Delete = version.Capabilities.Delete,
                StableVersions = version.Capabilities.StableVersions,
                DurableRetention = version.Capabilities.DurableRetention
            },
            Limits = new(version.Limits.MaximumPageSize, version.Limits.MaximumReadBytes, version.Limits.MaximumWriteBytes),
            Asp = new(version.Asp.Profile, version.Asp.McpServer, version.Asp.ToolPrefix, version.Asp.ProviderId,
            version.Asp.ProviderVersion, version.Asp.BindingPath),
            Contribution = new(provider, providerUid, generation, contributionId, version.Version, AepStorageProtocol.Digest(version)),
            Deprecated = version.Deprecated,
            Documents = version.Documents is { } documents ? new(documents.Get, documents.Put, documents.Delete, documents.Query,
                documents.ImmutablePublications, documents.QueryProfiles.Select(p => new StorageQueryProfile(p.Id, p.Version, p.Encoding)).ToArray()) : null
        };
}
