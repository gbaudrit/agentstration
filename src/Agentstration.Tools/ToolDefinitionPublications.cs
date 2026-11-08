using System.Globalization;
using System.Text.Json;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Parameters;
using Agentstration.Secrets.Abstractions;

namespace Agentstration.Tools;

public sealed partial class ToolDefinitionService
{
    public static string VersionName(string name, string version) => $"{name}.v{version}";

    public async Task<IReadOnlyList<ToolDefinitionUsage>> ListUsagesAsync(string name, ResourceNamespace @namespace,
        CancellationToken cancellationToken)
    {
        var definition = await GetAsync(name, @namespace, cancellationToken)
            ?? throw new ResourceNotFoundException(new(ToolResourceKinds.ToolDefinition, name, @namespace));
        return usages is null ? [] : await usages.ReadAsync(definition.Value,
            await ListVersionsAsync(name, @namespace, cancellationToken), cancellationToken);
    }

    public async Task<IReadOnlyList<StoredResource<ToolDefinitionResource>>> ListPublishedAsync(CancellationToken cancellationToken)
    {
        var result = new List<StoredResource<ToolDefinitionResource>>();
        foreach (var version in await store.ListAllAsync<ToolDefinitionVersionResource>(ToolResourceKinds.ToolDefinitionVersion, cancellationToken))
        {
            var owner = await GetExactAsync(version.Value.DefinitionName, version.Value.Namespace, version.Value.ScopeRef!.Value, cancellationToken);
            if (owner is null || owner.Value.Uid != version.Value.DefinitionUid || !owner.Value.Definition.Enabled) continue;
            result.Add(new(new ToolDefinitionResource
            {
                ApiVersion = version.Value.ApiVersion, Kind = ToolResourceKinds.ToolDefinition,
                Metadata = version.Value.Metadata, ScopeRef = version.Value.ScopeRef, Uid = version.Value.DefinitionUid,
                Generation = version.Value.Generation, Definition = version.Value.Definition
            }, version.ETag, version.UpdatedAt));
        }
        return result;
    }

    public async Task<IReadOnlyList<ToolDefinitionVersionResource>> ListVersionsAsync(string name,
        ResourceNamespace @namespace, CancellationToken cancellationToken)
    {
        var definition = await GetAsync(name, @namespace, cancellationToken)
            ?? throw new ResourceNotFoundException(new(ToolResourceKinds.ToolDefinition, name, @namespace));
        return (await store.ListAllAsync<ToolDefinitionVersionResource>(ToolResourceKinds.ToolDefinitionVersion, cancellationToken))
            .Select(value => value.Value).Where(value => value.DefinitionUid == definition.Value.Uid
                && value.ScopeRef == definition.Value.ScopeRef && value.Namespace == @namespace)
            .OrderByDescending(value => value.PublishedAt).ThenBy(value => value.Name, StringComparer.Ordinal).ToArray();
    }

    public async Task<StoredResource<ToolDefinitionResource>?> GetInvocationAsync(string name,
        ResourceNamespace @namespace, CancellationToken cancellationToken, ResourceScopeRef? ownerScope = null)
    {
        var publication = ownerScope is { } exact
            ? await store.GetExactAsync<ToolDefinitionVersionResource>(ScopedResourceAddress.Create(exact, @namespace, ToolResourceKinds.ToolDefinitionVersion, name), cancellationToken)
            : await store.GetAsync<ToolDefinitionVersionResource>(new(ToolResourceKinds.ToolDefinitionVersion, name, @namespace), cancellationToken);
        if (publication is null) return null;
        var version = publication.Value;
        var owner = await GetExactAsync(version.DefinitionName, @namespace, version.ScopeRef!.Value, cancellationToken);
        if (owner is null || owner.Value.Uid != version.DefinitionUid || !owner.Value.Definition.Enabled)
            throw new ToolDefinitionInvocationException("tool_definition_disabled", "The owning ToolDefinition is disabled, removed or replaced.");
        return new(new ToolDefinitionResource
        {
            ApiVersion = version.ApiVersion, Kind = ToolResourceKinds.ToolDefinition,
            Metadata = version.Metadata, ScopeRef = version.ScopeRef,
            Uid = version.DefinitionUid, Generation = version.Generation,
            Definition = version.Definition
        }, publication.ETag, publication.UpdatedAt);
    }

    public Task<StoredResource<ToolDefinitionVersionResource>?> GetPublicationAsync(string name,
        ResourceNamespace @namespace, ResourceScopeRef scope, CancellationToken cancellationToken) =>
        store.GetExactAsync<ToolDefinitionVersionResource>(ScopedResourceAddress.Create(scope, @namespace,
            ToolResourceKinds.ToolDefinitionVersion, name), cancellationToken);

    public async Task<ToolDefinitionVersionResource> PublishAsync(string name, ResourceNamespace @namespace,
        string version, CancellationToken cancellationToken, string? expectedETag = null)
    {
        ValidateVersion(version);
        var draft = await GetAsync(name, @namespace, cancellationToken)
            ?? throw new ResourceNotFoundException(new(ToolResourceKinds.ToolDefinition, name, @namespace));
        if (expectedETag is not null && draft.ETag != expectedETag)
            throw new ResourceConcurrencyException("The draft changed after publication review. Reload and review its changes.");
        if (draft.Value.Definition.Version != version)
            throw new ToolDefinitionValidationException("tool_definition_version_mismatch", "Publish the version selected in the draft.");
        var scope = draft.Value.ScopeRef!.Value;
        ToolRelaySource? source = null;
        var properties = draft.Value.Definition with { Publish = false, Enabled = true };
        if (properties is RelayToolDefinitionProperties relay)
        {
            source = await ResolveRelaySourceAsync(relay.Target, scope, cancellationToken);
            properties = relay with
            {
                Publish = false,
                Enabled = true,
                InputSchema = ToolRelayArguments.PublicSchema(source.Tool.Definition.Schema!.Input, relay.Bindings),
                OutputSchema = source.Tool.Definition.Schema.Output?.Clone(),
                RequiresApproval = relay.RequiresApproval || source.Tool.Definition.RequiresApproval
            };
        }
        var identities = new List<ToolBindingIdentity>();
        var relayDepth = source is null ? 0 : 1;
        if (source?.Tool.Definition.Metadata.TryGetValue("agentstration.relayDepth", out var depth) == true)
        {
            if (depth.ValueKind != JsonValueKind.Number || !depth.TryGetInt32(out var sourceDepth) || sourceDepth is < 0 or > 8)
                throw new ToolDefinitionValidationException("tool_definition_relay_depth", "The source has an invalid relay depth.");
            relayDepth += sourceDepth;
        }
        if (relayDepth is < 0 or > 8)
            throw new ToolDefinitionValidationException("tool_definition_relay_depth", "Relay composition is limited to eight levels.");
        foreach (var binding in properties.Bindings)
        {
            switch (binding)
            {
                case ParameterToolArgumentBinding parameter:
                    if (parameter.Parameter.Address.Kind != ParameterResourceKinds.Parameter || parameters is null)
                        throw new ToolDefinitionValidationException("tool_definition_binding_invalid", "Parameter resolution requires a Parameter reference.");
                    var value = await parameters.ResolveAsync(parameter.Parameter, new(scope, draft.Value.Address), cancellationToken)
                        ?? throw new ToolDefinitionValidationException("tool_definition_binding_unavailable", "The bound Parameter is unavailable.");
                    try { ToolInputSchemaValidator.Validate(ToolRelayArguments.PropertySchema(source!.Tool.Definition.Schema!.Input, binding.Path), value.Value); }
                    catch (ToolInputValidationException) { throw new ToolDefinitionValidationException("tool_definition_binding_invalid", "A Parameter value does not satisfy the source contract."); }
                    break;
                case SecretToolArgumentBinding secret:
                    if (secret.Secret.Address.Kind != "Secret" || secretAccess is null
                        || await secretAccess.GetAuthorizedStatusAsync(secret.Secret, new(scope, draft.Value.Address), cancellationToken) != SecretValueStatus.Configured)
                        throw new ToolDefinitionValidationException("tool_definition_binding_unavailable", "The Secret reference is not authorized and configured.");
                    break;
            }
            var address = binding switch
            {
                ParameterToolArgumentBinding parameter => ScopedResourceAddress.Create(parameter.Parameter.ScopeRef,
                    parameter.Parameter.Address.Namespace, parameter.Parameter.Address.Kind, parameter.Parameter.Address.Name),
                SecretToolArgumentBinding secret when secret.Secret.ScopeRef is { } owner => ScopedResourceAddress.Create(owner,
                    secret.Secret.Address.Namespace, secret.Secret.Address.Kind, secret.Secret.Address.Name),
                LiteralToolArgumentBinding => (ScopedResourceAddress?)null,
                _ => throw new ToolDefinitionValidationException("tool_definition_binding_invalid", "Bindings require an exact scoped resource reference.")
            };
            if (address is null) continue;
            var identity = await BindingIdentityAsync(address.Value, cancellationToken);
            identities.Add(new(binding.Path, identity));
        }
        var existing = await GetPublicationAsync(VersionName(name, version), @namespace, scope, cancellationToken);
        ToolDefinitionVersionResource publication;
        if (existing is not null)
        {
            if (existing.Value.DefinitionUid != draft.Value.Uid
                || !SameSchema(JsonSerializer.SerializeToElement<ToolDefinitionProperties>(existing.Value.Definition),
                    JsonSerializer.SerializeToElement<ToolDefinitionProperties>(properties))
                || !existing.Value.BindingIdentities.SequenceEqual(identities))
                throw new ToolDefinitionValidationException("tool_definition_version_immutable", "This version is already published. Choose a new version for changed configuration.");
            publication = existing.Value;
        }
        else
        {
            publication = (await store.CreateImmutableAsync(new ToolDefinitionVersionResource
            {
                ApiVersion = draft.Value.ApiVersion, Kind = ToolResourceKinds.ToolDefinitionVersion,
                Metadata = draft.Value.Metadata with { Name = VersionName(name, version) }, ScopeRef = scope,
                DefinitionName = name, DefinitionUid = draft.Value.Uid, Definition = properties,
                Source = source, BindingIdentities = identities, RelayDepth = relayDepth, PublishedAt = timeProvider.GetUtcNow(),
                Generation = 1, Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
            }, cancellationToken)).Value;
        }
        var key = ScopedResourceAddress.Create(scope, @namespace, ToolResourceKinds.Tool,
            AgentstrationToolProvider.ToolResourceName(publication.Name));
        if (await store.GetExactAsync<ToolResource>(key, cancellationToken) is null)
            await MaterializeAsync(new ToolDefinitionResource
            {
                ApiVersion = publication.ApiVersion, Kind = ToolResourceKinds.ToolDefinition,
                Metadata = publication.Metadata, ScopeRef = scope, Uid = publication.Uid,
                Generation = 1, Definition = publication.Definition
            }, cancellationToken, publication.DefinitionUid, publication.RelayDepth);
        return publication;
    }

    public async Task<Guid> BindingIdentityAsync(ScopedResourceAddress address, CancellationToken cancellationToken)
    {
        // Exact scoped inventory is authorized by the existing store; never look up a resource by name across scopes.
        const int pageSize = 100;
        for (var skip = 0; ; skip += pageSize)
        {
            var page = await store.ListExactInventoryAsync(address.ScopeRef, skip, pageSize, cancellationToken);
            var identity = page.FirstOrDefault(value => value.Kind == address.Kind && value.Namespace == address.Namespace && value.Name == address.Name);
            if (identity is not null) return identity.Uid;
            if (page.Count < pageSize) break;
        }
        throw new ToolDefinitionValidationException("tool_definition_binding_unavailable", "A bound resource is missing or inaccessible.");
    }

    public async Task<ToolRelaySource> ResolveRelaySourceAsync(ToolRelayTarget target, ResourceScopeRef consumerScope,
        CancellationToken cancellationToken)
    {
        var reference = new ResourceReference(target.Name, target.ScopeRef, target.Namespace);
        var tool = references is not null
            ? await references.ResolveAsync<ToolResource>(reference, target.Namespace, ToolResourceKinds.Tool, consumerScope, cancellationToken)
            : target.ScopeRef == consumerScope ? await store.GetExactAsync<ToolResource>(ScopedResourceAddress.Create(target.ScopeRef,
                target.Namespace, ToolResourceKinds.Tool, target.Name), cancellationToken) : null;
        if (tool is null || tool.Value.Uid != target.Uid || tool.Value.Generation != target.Generation)
            throw new ToolDefinitionValidationException("tool_definition_target_stale", "The source Tool no longer matches the selected exact identity and revision.");
        if (!tool.Value.Definition.Enabled || tool.Value.Definition.Discovery?.Available != true || tool.Value.Definition.Schema is null)
            throw new ToolDefinitionValidationException("tool_definition_target_unavailable", "The source Tool is disabled, unavailable or has no contract.");
        var providerReference = tool.Value.Definition.Provider
            ?? throw new ToolDefinitionValidationException("tool_definition_target_invalid", "The source Tool has no provider.");
        var providerScope = providerReference.ScopeRef ?? target.ScopeRef;
        var providerNamespace = providerReference.Namespace ?? target.Namespace;
        var provider = await store.GetExactAsync<ToolProviderResource>(ScopedResourceAddress.Create(providerScope,
            providerNamespace, ToolResourceKinds.ToolProvider, providerReference.Name), cancellationToken);
        if (provider is null || !provider.Value.Definition.Enabled)
            throw new ToolDefinitionValidationException("tool_definition_target_unavailable", "The source provider is unavailable.");
        return new(tool.Value, provider.Value);
    }

    private static void ValidateVersion(string version)
    {
        var parts = version.Split('.');
        if (parts.Length != 3 || parts.Any(part => part.Length == 0 || part.Length > 9
            || part.Length > 1 && part[0] == '0' || !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
            throw new ToolDefinitionValidationException("tool_definition_version_invalid", "Use an explicit major.minor.patch version.");
    }
}
