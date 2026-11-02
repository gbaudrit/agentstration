using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.DataSources;
using Agentstration.DataSources.Contracts;
using Agentstration.Extensions.Aep;
using Agentstration.Extensions.Contracts;
using Agentstration.Flows;
using Agentstration.Flows.Application;
using Agentstration.Infrastructure.Declarative;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Infrastructure.Bootstrap;

public sealed class AepBundleConflictException(string code, string message) : InvalidOperationException($"{code}: {message}")
{
    public string Code { get; } = code;
}

public sealed class AepEnrollmentBundleProvisioner(
    IServiceScopeFactory scopes,
    IResourceStore store,
    ToolManagementService tools,
    ToolSetService toolSets,
    DataSourceProfileService profiles,
    FlowService flows,
    IResourceGovernanceContext governance) : IAepEnrollmentResourceProvisioner
{
    public async Task ProvisionAsync(
        ExtensionRegistrationResource registration,
        AepManifest? authenticatedManifest,
        CancellationToken cancellationToken)
    {
        if (authenticatedManifest is null) return;
        var scopeRef = registration.ScopeRef
            ?? throw new InvalidOperationException("The Extension Registration has no ownership scope.");
        foreach (var bundle in authenticatedManifest.Contributions.DataSourceProfileBundles ?? [])
            await InstallAsync(registration, authenticatedManifest, bundle, scopeRef, cancellationToken);
    }

    private async Task InstallAsync(
        ExtensionRegistrationResource registration,
        AepManifest manifest,
        AepDataSourceProfileBundleContribution bundle,
        ResourceScopeRef scopeRef,
        CancellationToken cancellationToken)
    {
        var name = InstallationName(registration, bundle);
        var address = ScopedResourceAddress.Create(scopeRef, registration.Namespace,
            ExtensionKinds.ExtensionBundleInstallation, name);
        var installationReference = address.ToString();
        var authority = ResourceGovernance.Extension(
            installationReference,
            manifest.Extension.Id,
            manifest.Extension.Version);
        var digest = Digest(bundle);
        var bindings = await ResolveToolBindingsAsync(registration, bundle, scopeRef, cancellationToken);
        var existing = await store.GetExactAsync<ExtensionBundleInstallationResource>(address, cancellationToken);
        if (existing is not null && existing.Value.Governance != ResourceGovernance.Extension(installationReference,
                existing.Value.Definition.ExtensionId, existing.Value.Definition.ExtensionVersion))
            throw Conflict("owner_conflict", $"Installation '{name}' has another management authority.");

        using var authorityScope = governance.Push(authority);
        var installing = existing?.Value ?? Installation(registration, manifest, bundle, scopeRef, name, digest, bindings,
            ExtensionBundleInstallationState.Installing, [], null);
        StoredResource<ExtensionBundleInstallationResource>? stored = existing;
        var validated = false;
        try
        {
            if (existing is not null)
                EnsureSameInstallation(existing.Value, registration, manifest, bundle, digest, bindings);
            await EnsureResourcesAreOwnedOrAbsentAsync(bundle, scopeRef, installationReference, cancellationToken);
            var current = await InventoryAsync(bundle, scopeRef, installationReference, allowMissing: true, cancellationToken);
            if (existing is not null)
            {
                EnsureInventoryMatches(existing.Value.Definition.ManagedResources, current);
            }
            await EnsureDefinitionsMatchAsync(AepDataSourceProfileBundleInstaller.ResolveDocuments(bundle, bindings), scopeRef, cancellationToken);
            if (existing is not null)
            {
                if (existing.Value.Definition.State == ExtensionBundleInstallationState.Installed)
                {
                    var complete = await InventoryAsync(bundle, scopeRef, installationReference, allowMissing: false, cancellationToken);
                    if (complete.Count != existing.Value.Definition.ManagedResources.Count)
                        throw Conflict("resource_drift", "The installed resource inventory has changed.");
                    return;
                }
            }
            validated = true;
            installing = installing with
            {
                Definition = installing.Definition with
                {
                    State = ExtensionBundleInstallationState.Installing,
                    ManagedResources = current,
                    Diagnostic = null
                }
            };
            stored = await store.PutExactAsync(scopeRef, installing, existing?.ETag, existing is null, cancellationToken);
            using var serviceScope = scopes.CreateScope();
            var installer = serviceScope.ServiceProvider.GetRequiredService<AepDataSourceProfileBundleInstaller>();
            _ = await installer.InstallAsync(bundle, Target(scopeRef), bindings, cancellationToken,
                (document, token) => CompletePublicationsAsync(document, scopeRef, token));
            var inventory = await InventoryAsync(bundle, scopeRef, installationReference, allowMissing: false, cancellationToken);
            var installed = installing with
            {
                Definition = installing.Definition with
                {
                    State = ExtensionBundleInstallationState.Installed,
                    ManagedResources = inventory
                }
            };
            _ = await store.PutExactAsync(scopeRef, installed, stored.ETag, false, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var inventory = validated
                ? await InventoryAsync(bundle, scopeRef, installationReference, allowMissing: true, cancellationToken)
                : existing?.Value.Definition.ManagedResources ?? [];
            var failed = installing with
            {
                Definition = installing.Definition with
                {
                    State = existing is not null && (exception is AepBundleConflictException
                        || existing.Value.Definition.State is ExtensionBundleInstallationState.Installed or ExtensionBundleInstallationState.Degraded)
                        ? ExtensionBundleInstallationState.Degraded : ExtensionBundleInstallationState.Failed,
                    ManagedResources = inventory,
                    Diagnostic = exception.Message
                }
            };
            using var failureAuthority = governance.Push(existing?.Value.Governance ?? authority);
            try { _ = await store.PutExactAsync(scopeRef, failed, stored?.ETag, stored is null, cancellationToken); }
            catch (ResourceConcurrencyException) { }
            throw;
        }
    }

    private async Task<IReadOnlyDictionary<string, ResourceReference>> ResolveToolBindingsAsync(
        ExtensionRegistrationResource registration,
        AepDataSourceProfileBundleContribution bundle,
        ResourceScopeRef scopeRef,
        CancellationToken cancellationToken)
    {
        var candidates = (await tools.ListToolsAsync(cancellationToken))
            .Where(value => value.Value.ScopeRef == scopeRef
                && value.Value.Definition.Provider is { } provider
                && string.Equals(provider.Name, registration.Name, StringComparison.Ordinal)
                && (provider.Namespace ?? registration.Namespace) == registration.Namespace)
            .ToArray();
        var result = new Dictionary<string, ResourceReference>(StringComparer.Ordinal);
        foreach (var required in bundle.RequiredTools ?? [])
        {
            var matches = candidates.Where(value =>
                string.Equals(value.Value.Definition.ExternalId, required, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException(
                    $"Data Source Profile bundle '{bundle.Id}' requires Tool contribution '{required}' from registration '{registration.Name}'.");
            var tool = matches[0].Value;
            result.Add(required, new ResourceReference(tool.Name, scopeRef, tool.Namespace));
        }
        return result;
    }

    private async Task EnsureResourcesAreOwnedOrAbsentAsync(
        AepDataSourceProfileBundleContribution bundle,
        ResourceScopeRef scopeRef,
        string installationReference,
        CancellationToken cancellationToken)
    {
        foreach (var document in Documents(bundle))
        {
            ResourceGovernance? resourceGovernance = document.Kind switch
            {
                ToolResourceKinds.ToolSet => (await toolSets.GetExactAsync(scopeRef, document.Metadata.Namespace,
                    document.Metadata.Name, cancellationToken))?.Value.Governance,
                DataSourceResourceKinds.DataSourceProfile => (await profiles.GetAsync(document.Metadata.Namespace,
                    document.Metadata.Name, scopeRef, cancellationToken))?.Value.Governance,
                FlowResourceKinds.Flow => (await flows.GetExactAsync(scopeRef,
                    new(document.Metadata.Name, document.Metadata.Namespace), cancellationToken))?.Value.Governance,
                _ => null
            };
            if (resourceGovernance is not null
                && (resourceGovernance.ManagedBy?.Kind != ResourceManagementAuthorityKind.ExtensionInstallation
                    || !string.Equals(resourceGovernance.ManagedBy.Reference, installationReference, StringComparison.Ordinal)))
                throw Conflict("owner_conflict",
                    $"AEP bundle resource '{document.Kind}/{document.Metadata.Namespace}/{document.Metadata.Name}' conflicts with an existing resource owned by another authority.");
        }
    }

    private async Task<IReadOnlyList<ExtensionManagedResource>> InventoryAsync(
        AepDataSourceProfileBundleContribution bundle,
        ResourceScopeRef scopeRef,
        string installationReference,
        bool allowMissing,
        CancellationToken cancellationToken)
    {
        var result = new List<ExtensionManagedResource>();
        foreach (var document in Documents(bundle))
        {
            var version = Version(document);
            if (document.Kind == ToolResourceKinds.ToolSet)
            {
                var draft = (await toolSets.GetExactAsync(scopeRef, document.Metadata.Namespace,
                    document.Metadata.Name, cancellationToken))?.Value;
                if (draft is null || !OwnedBy(draft.Governance, installationReference))
                {
                    if (allowMissing) continue;
                    throw Conflict("resource_missing", $"ToolSet '{document.Metadata.Name}' is missing or has another owner.");
                }
                result.Add(Evidence(draft, scopeRef, draft.Definition.Version,
                    Sha256(JsonSerializer.Serialize(draft.Definition, AepProtocol.JsonOptions)), false));
                if (Publish(document) && version is not null)
                {
                    var published = (await toolSets.GetVersionExactAsync(scopeRef, document.Metadata.Namespace,
                        document.Metadata.Name, version, cancellationToken))?.Value;
                    if (published is not null && OwnedBy(published.Governance, installationReference))
                        result.Add(Evidence(published, scopeRef, published.Version, published.DefinitionHash, true));
                    else if (!allowMissing) throw Conflict("resource_missing", $"ToolSet version '{document.Metadata.Name}:{version}' is missing or has another owner.");
                }
            }
            else if (document.Kind == DataSourceResourceKinds.DataSourceProfile)
            {
                var draft = (await profiles.GetAsync(document.Metadata.Namespace, document.Metadata.Name,
                    scopeRef, cancellationToken))?.Value;
                if (draft is null || !OwnedBy(draft.Governance, installationReference))
                {
                    if (allowMissing) continue;
                    throw Conflict("resource_missing", $"Data Source Profile '{document.Metadata.Name}' is missing or has another owner.");
                }
                result.Add(Evidence(draft, scopeRef, draft.Definition.Version,
                    Sha256(JsonSerializer.Serialize(draft.Definition, AepProtocol.JsonOptions)), false));
                if (version is not null)
                {
                    var published = (await profiles.GetRevisionAsync(document.Metadata.Namespace,
                        document.Metadata.Name, version, scopeRef, cancellationToken))?.Value;
                    if (published is not null && OwnedBy(published.Governance, installationReference))
                        result.Add(Evidence(published, scopeRef, published.Version, published.DefinitionHash, true));
                    else if (!allowMissing) throw Conflict("resource_missing", $"Profile revision '{document.Metadata.Name}:{version}' is missing or has another owner.");
                }
            }
            else if (document.Kind == FlowResourceKinds.Flow)
            {
                var id = new FlowId(document.Metadata.Name, document.Metadata.Namespace);
                var draft = (await flows.GetExactAsync(scopeRef, id, cancellationToken))?.Value;
                if (draft is null || !OwnedBy(draft.Governance, installationReference))
                {
                    if (allowMissing) continue;
                    throw Conflict("resource_missing", $"Flow '{id}' is missing or has another owner.");
                }
                result.Add(new ExtensionManagedResource
                {
                    Kind = FlowResourceKinds.Flow,
                    Namespace = id.Namespace,
                    Name = id.Value,
                    ScopeRef = scopeRef,
                    Uid = draft.Uid,
                    Version = draft.Version,
                    DefinitionHash = Hash(new { Definition = FlowDraftHash(draft), draft.ActiveVersion })
                });
                if (Publish(document) && version is not null)
                {
                    var published = (await flows.GetVersionExactAsync(scopeRef, id, version, cancellationToken))?.Value;
                    if (published is not null && OwnedBy(published.Governance, installationReference))
                        result.Add(new ExtensionManagedResource
                        {
                            Kind = "FlowVersion",
                            Namespace = id.Namespace,
                            Name = id.Value,
                            ScopeRef = scopeRef,
                            Version = version,
                            DefinitionHash = published.DefinitionHash,
                            Published = true
                        });
                    else if (!allowMissing) throw Conflict("resource_missing", $"Flow version '{id}:{version}' is missing or has another owner.");
                }
            }
        }
        return result;
    }

    private async Task CompletePublicationsAsync(BootstrapResourceDocument document, ResourceScopeRef scopeRef,
        CancellationToken cancellationToken)
    {
        var version = Version(document);
        if (version is null) return;
        if (document.Kind == ToolResourceKinds.ToolSet && Publish(document)
            && await toolSets.GetVersionExactAsync(scopeRef, document.Metadata.Namespace, document.Metadata.Name, version, cancellationToken) is null)
            _ = await toolSets.PublishExactAsync(scopeRef, document.Metadata.Namespace, document.Metadata.Name, version, cancellationToken);
        else if (document.Kind == FlowResourceKinds.Flow && Publish(document))
        {
            var id = new FlowId(document.Metadata.Name, document.Metadata.Namespace);
            if (await flows.GetVersionExactAsync(scopeRef, id, version, cancellationToken) is null)
            {
                var activate = !document.Definition.TryGetProperty("activate", out var value) || value.ValueKind == JsonValueKind.True;
                _ = await flows.PublishVersionExactAsync(scopeRef, id, version, activate, cancellationToken);
            }
        }
        else if (document.Kind == DataSourceResourceKinds.DataSourceProfile
            && await profiles.GetRevisionAsync(document.Metadata.Namespace, document.Metadata.Name, version, scopeRef, cancellationToken) is null)
            _ = await profiles.PublishAsync(document.Metadata.Namespace, document.Metadata.Name, scopeRef, new(version, true), cancellationToken);
    }

    private async Task EnsureDefinitionsMatchAsync(IReadOnlyList<BootstrapResourceDocument> documents,
        ResourceScopeRef scopeRef, CancellationToken cancellationToken)
    {
        foreach (var document in documents)
        {
            var matches = true;
            if (document.Kind == ToolResourceKinds.ToolSet)
            {
                var current = await toolSets.GetExactAsync(scopeRef, document.Metadata.Namespace, document.Metadata.Name, cancellationToken);
                matches = current is null || Hash(current.Value.Definition) == Hash(WorkspaceBootstrapResource.Parse<ToolSetResource>(document).Definition);
            }
            else if (document.Kind == DataSourceResourceKinds.DataSourceProfile)
            {
                var current = await profiles.GetAsync(document.Metadata.Namespace, document.Metadata.Name, scopeRef, cancellationToken);
                matches = current is null || Hash(current.Value.Definition) == Hash(WorkspaceBootstrapResource.Parse<DataSourceProfileResource>(document).Definition);
            }
            else if (document.Kind == FlowResourceKinds.Flow)
            {
                var current = await flows.GetExactAsync(scopeRef, new(document.Metadata.Name, document.Metadata.Namespace), cancellationToken);
                if (current is not null)
                {
                    var desired = WorkspaceBootstrapResource.Parse<DeclarativeResourceEnvelope<DeclarativeFlowDefinition>>(document).Definition;
                    matches = FlowDraftHash(current.Value) == FlowDraftHash(current.Value with
                    {
                        Name = document.Metadata.Name,
                        Description = desired.Description,
                        Version = desired.Version,
                        Enabled = desired.Enabled,
                        Definition = desired.Spec,
                        Graph = desired.Graph,
                        Metadata = desired.Metadata,
                        DisplayName = desired.DisplayName
                    });
                }
            }
            if (!matches) throw Conflict("resource_drift", $"Resource '{document.Kind}/{document.Metadata.Namespace}/{document.Metadata.Name}' differs from its bundle manifest.");
        }
    }

    private static string FlowDraftHash(FlowResource value) => Hash(new
    {
        value.Definition,
        value.Graph,
        value.Metadata,
        value.Version,
        value.Enabled,
        value.Name,
        value.Description,
        value.DisplayName
    });

    private static string Hash<T>(T value) => Sha256(JsonSerializer.Serialize(value, AepProtocol.JsonOptions));

    private bool OwnedBy(ResourceGovernance value, string reference) =>
        value.ManagedBy?.Kind == ResourceManagementAuthorityKind.ExtensionInstallation
        && value.ManagedBy.Reference == reference && value == governance.Current;

    private static void EnsureInventoryMatches(IReadOnlyList<ExtensionManagedResource> expected,
        IReadOnlyList<ExtensionManagedResource> actual)
    {
        foreach (var resource in expected)
        {
            var current = actual.SingleOrDefault(value => value.Kind == resource.Kind && value.Namespace == resource.Namespace
                && value.Name == resource.Name && value.ScopeRef == resource.ScopeRef && value.Published == resource.Published);
            if (current != resource)
                throw Conflict("resource_drift", $"Governed resource '{resource.Kind}/{resource.Namespace}/{resource.Name}' no longer matches its recorded identity, version or definition.");
        }
    }

    private static AepBundleConflictException Conflict(string reason, string message) => new($"extension_bundle_{reason}", message);

    private static ExtensionManagedResource Evidence(
        Resource resource,
        ResourceScopeRef scopeRef,
        string? version,
        string? definitionHash,
        bool published) => new()
        {
            Kind = resource.Kind,
            Namespace = resource.Namespace,
            Name = resource.Name,
            ScopeRef = scopeRef,
            Uid = resource.Uid,
            Generation = resource.Generation,
            Version = version,
            DefinitionHash = definitionHash,
            Published = published
        };

    private static ExtensionBundleInstallationResource Installation(
        ExtensionRegistrationResource registration,
        AepManifest manifest,
        AepDataSourceProfileBundleContribution bundle,
        ResourceScopeRef scopeRef,
        string name,
        string digest,
        IReadOnlyDictionary<string, ResourceReference> bindings,
        ExtensionBundleInstallationState state,
        IReadOnlyList<ExtensionManagedResource> inventory,
        string? diagnostic) => new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ExtensionKinds.ExtensionBundleInstallation,
            Metadata = new ResourceMetadata { Name = name, Namespace = registration.Namespace },
            ScopeRef = scopeRef,
            Generation = 1,
            Definition = new ExtensionBundleInstallationProperties
            {
                Registration = new ResourceReference(registration.Name, scopeRef, registration.Namespace),
                ExtensionId = manifest.Extension.Id,
                ExtensionVersion = manifest.Extension.Version,
                BundleId = bundle.Id,
                BundleVersion = bundle.Version,
                BundleDigest = digest,
                State = state,
                ToolBindings = bindings,
                ManagedResources = inventory,
                Diagnostic = diagnostic
            }
        };

    private static void EnsureSameInstallation(
        ExtensionBundleInstallationResource existing,
        ExtensionRegistrationResource registration,
        AepManifest manifest,
        AepDataSourceProfileBundleContribution bundle,
        string digest,
        IReadOnlyDictionary<string, ResourceReference> bindings)
    {
        var value = existing.Definition;
        if (!string.Equals(value.ExtensionId, manifest.Extension.Id, StringComparison.Ordinal)
            || !string.Equals(value.ExtensionVersion, manifest.Extension.Version, StringComparison.Ordinal)
            || !string.Equals(value.BundleId, bundle.Id, StringComparison.Ordinal)
            || !string.Equals(value.BundleVersion, bundle.Version, StringComparison.Ordinal)
            || !string.Equals(value.BundleDigest, digest, StringComparison.Ordinal)
            || value.Registration != new ResourceReference(registration.Name, registration.ScopeRef, registration.Namespace)
            || !BindingsEqual(value.ToolBindings, bindings))
            throw Conflict("installation_conflict",
                $"AEP bundle installation '{existing.Name}' conflicts with different owner, version, definition, or Tool bindings.");
    }

    private static bool BindingsEqual(
        IReadOnlyDictionary<string, ResourceReference> left,
        IReadOnlyDictionary<string, ResourceReference> right) =>
        left.Count == right.Count && left.All(value => right.TryGetValue(value.Key, out var other) && other == value.Value);

    private static IReadOnlyList<BootstrapResourceDocument> Documents(AepDataSourceProfileBundleContribution bundle) =>
        bundle.ResourceManifests.Select(ResourceManifestSerializer.FromYaml<BootstrapResourceDocument>).ToArray();

    private static string? Version(BootstrapResourceDocument document) =>
        document.Definition.TryGetProperty("version", out var version) ? version.GetString() : null;

    private static bool Publish(BootstrapResourceDocument document) =>
        document.Definition.TryGetProperty("publish", out var publish) ? publish.ValueKind == JsonValueKind.True
            : document.Kind == FlowResourceKinds.Flow;

    private static BootstrapApplicationTarget Target(ResourceScopeRef scopeRef) => scopeRef.Kind switch
    {
        ResourceScopeKind.Instance => new(),
        ResourceScopeKind.Tenant => new(TenantId: scopeRef.TargetId),
        ResourceScopeKind.Workspace => new(WorkspaceId: scopeRef.TargetId),
        _ => throw new InvalidOperationException($"Unsupported extension scope '{scopeRef}'.")
    };

    private static string InstallationName(
        ExtensionRegistrationResource registration,
        AepDataSourceProfileBundleContribution bundle) =>
        $"aep-{Sha256($"{registration.ScopeRef}|{registration.Namespace}|{registration.Name}|{bundle.Id}|{bundle.Version}")[..24]}";

    private static string Digest(AepDataSourceProfileBundleContribution bundle) =>
        Sha256(JsonSerializer.Serialize(bundle, AepProtocol.JsonOptions));

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
