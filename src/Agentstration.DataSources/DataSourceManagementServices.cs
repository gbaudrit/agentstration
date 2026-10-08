using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.DataSources.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Security.Contracts;

namespace Agentstration.DataSources;

public sealed class DataSourceValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class DataSourceProfileService(
    IResourceStore store,
    IResourceReferenceResolver references,
    IResourceScopeOperations scopeOperations,
    ICurrentRequestContext requestContext,
    ISecurityAuditWriter audit,
    TimeProvider timeProvider)
{
    public const int MaximumConfigurationSchemaBytes = 64 * 1024;

    public Task<IReadOnlyList<StoredResource<DataSourceProfileResource>>> ListAsync(CancellationToken cancellationToken) =>
        store.ListAllAsync<DataSourceProfileResource>(DataSourceResourceKinds.DataSourceProfile, cancellationToken);

    public Task<StoredResource<DataSourceProfileResource>?> GetAsync(
        ResourceNamespace @namespace,
        string name,
        ResourceScopeRef? scopeRef,
        CancellationToken cancellationToken) => scopeRef is { } exact
            ? store.GetExactAsync<DataSourceProfileResource>(Address(exact, @namespace,
                DataSourceResourceKinds.DataSourceProfile, name), cancellationToken)
            : store.GetAsync<DataSourceProfileResource>(new(DataSourceResourceKinds.DataSourceProfile, name, @namespace), cancellationToken);

    public async Task<StoredResource<DataSourceProfileResource>> CreateAsync(
        DataSourceProfileResource resource,
        CancellationToken cancellationToken)
    {
        EnsureProtectedMutationAllowed(resource);
        var scopeRef = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(DataSourceResourceKinds.DataSourceProfile);
        var desired = resource with
        {
            ScopeRef = scopeRef,
            Generation = 1,
            ActiveVersion = null,
            Status = Ready("DraftReady", "The Data Source Profile draft is valid.")
        };
        Validate(desired);
        var stored = await scopeOperations.WriteAsync(desired, scopeRef, AuthorizationPermissions.ResourcesWrite,
            token => store.PutExactAsync(scopeRef, desired, null, true, token), cancellationToken);
        await AuditAsync(SecurityAuditActions.DataSourceProfileCreated, scopeRef, stored.Value.Name, cancellationToken);
        return stored;
    }

    public async Task<StoredResource<DataSourceProfileResource>> PutAsync(
        ResourceNamespace @namespace,
        string name,
        ResourceScopeRef? scopeRef,
        DataSourceProfileProperties definition,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        var existing = await GetAsync(@namespace, name, scopeRef, cancellationToken) ?? throw NotFound(name, @namespace);
        EnsureProtectedMutationAllowed(existing.Value);
        var exact = RequireScope(existing.Value);
        var updated = existing.Value with
        {
            Generation = checked(existing.Value.Generation + 1),
            Definition = definition,
            Status = Ready("DraftReady", "The Data Source Profile draft is valid.")
        };
        Validate(updated);
        var stored = await scopeOperations.WriteAsync(updated, exact, AuthorizationPermissions.ResourcesWrite,
            token => store.PutExactAsync(exact, updated, ifMatch, false, token), cancellationToken);
        await AuditAsync(SecurityAuditActions.DataSourceProfileUpdated, exact, stored.Value.Name, cancellationToken);
        return stored;
    }

    public async Task<IReadOnlyList<StoredResource<DataSourceProfileRevisionResource>>> ListRevisionsAsync(
        ResourceNamespace @namespace,
        string name,
        ResourceScopeRef? scopeRef,
        CancellationToken cancellationToken)
    {
        var profile = await GetAsync(@namespace, name, scopeRef, cancellationToken) ?? throw NotFound(name, @namespace);
        var exact = RequireScope(profile.Value);
        return (await store.ListExactAsync<DataSourceProfileRevisionResource>(exact,
                DataSourceResourceKinds.DataSourceProfileRevision, 0, 1000, cancellationToken))
            .Where(value => value.Value.ProfileUid == profile.Value.Uid)
            .OrderByDescending(value => value.Value.PublishedAt)
            .ToArray();
    }

    public async Task<StoredResource<DataSourceProfileRevisionResource>?> GetRevisionAsync(
        ResourceNamespace @namespace,
        string name,
        string version,
        ResourceScopeRef? scopeRef,
        CancellationToken cancellationToken)
    {
        var profile = await GetAsync(@namespace, name, scopeRef, cancellationToken);
        if (profile is null) return null;
        return await GetRevisionCoreAsync(profile.Value, version, cancellationToken);
    }

    public async Task<StoredResource<DataSourceProfileRevisionResource>> PublishAsync(
        ResourceNamespace @namespace,
        string name,
        ResourceScopeRef? scopeRef,
        PublishDataSourceProfileRequest request,
        CancellationToken cancellationToken)
    {
        ValidateVersion(request.Version);
        var profile = await GetAsync(@namespace, name, scopeRef, cancellationToken) ?? throw NotFound(name, @namespace);
        EnsureProtectedMutationAllowed(profile.Value);
        var exact = RequireScope(profile.Value);
        var definition = profile.Value.Definition with { Version = request.Version };
        var hash = Hash(definition);
        var revision = new DataSourceProfileRevisionResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = DataSourceResourceKinds.DataSourceProfileRevision,
            Metadata = new ResourceMetadata
            {
                Name = RevisionName(profile.Value.Name, request.Version),
                Namespace = profile.Value.Namespace,
                Tags = profile.Value.Metadata.Tags,
                Annotations = profile.Value.Metadata.Annotations
            },
            ScopeRef = exact,
            Generation = 1,
            Status = Ready("Published", "The Data Source Profile revision is immutable."),
            ProfileUid = profile.Value.Uid,
            ProfileName = profile.Value.Name,
            ProfileGeneration = profile.Value.Generation,
            Version = request.Version,
            DefinitionHash = hash,
            PublishedAt = timeProvider.GetUtcNow(),
            PublishedBy = requestContext.IsInitialized ? requestContext.Current.PrincipalId : Guid.Empty,
            Definition = definition
        };
        var stored = await scopeOperations.WriteAsync(profile.Value, exact, AuthorizationPermissions.ResourcesWrite,
            async token =>
            {
                var existing = await GetRevisionCoreAsync(profile.Value, request.Version, token);
                if (existing is not null)
                {
                    if (!string.Equals(existing.Value.DefinitionHash, hash, StringComparison.Ordinal))
                        throw Error("data_source_profile_revision_conflict",
                            $"Profile revision '{name}:{request.Version}' already exists with another definition.");
                    return existing;
                }
                try
                {
                    return await store.CreateImmutableAsync(revision, token);
                }
                catch (ResourceConcurrencyException)
                {
                    var concurrent = await GetRevisionCoreAsync(profile.Value, request.Version, token);
                    if (concurrent is not null
                        && string.Equals(concurrent.Value.DefinitionHash, hash, StringComparison.Ordinal))
                        return concurrent;
                    throw Error("data_source_profile_revision_conflict",
                        $"Profile revision '{name}:{request.Version}' was published concurrently with another definition.");
                }
            }, cancellationToken);
        await AuditAsync(SecurityAuditActions.DataSourceProfileRevisionPublished, exact,
            $"{name}:{request.Version}", cancellationToken);
        if (request.Activate)
            _ = await ActivateAsync(@namespace, name, exact, new(request.Version), profile.ETag, cancellationToken);
        return stored;
    }

    public async Task<StoredResource<DataSourceProfileResource>> ActivateAsync(
        ResourceNamespace @namespace,
        string name,
        ResourceScopeRef? scopeRef,
        ActivateDataSourceProfileRequest request,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        var profile = await GetAsync(@namespace, name, scopeRef, cancellationToken) ?? throw NotFound(name, @namespace);
        EnsureProtectedMutationAllowed(profile.Value);
        var exact = RequireScope(profile.Value);
        _ = await GetRevisionCoreAsync(profile.Value, request.Version, cancellationToken)
            ?? throw Error("data_source_profile_revision_not_found",
                $"Published profile revision '{name}:{request.Version}' was not found.");
        var updated = profile.Value with
        {
            Generation = checked(profile.Value.Generation + 1),
            ActiveVersion = request.Version,
            Status = Ready("ActiveRevision", $"Profile revision '{request.Version}' is active.")
        };
        var stored = await scopeOperations.WriteAsync(profile.Value, exact, AuthorizationPermissions.ResourcesWrite,
            token => store.PutExactAsync(exact, updated, ifMatch, false, token), cancellationToken);
        await AuditAsync(SecurityAuditActions.DataSourceProfileRevisionActivated, exact,
            $"{name}:{request.Version}", cancellationToken);
        return stored;
    }

    public async Task<ResolvedDataSourceProfile> ResolveActiveAsync(
        ResourceScopeRef consumerScope,
        ResourceNamespace ownerNamespace,
        ResourceReference reference,
        CancellationToken cancellationToken)
    {
        var profile = await references.ResolveAsync<DataSourceProfileResource>(reference, ownerNamespace,
            DataSourceResourceKinds.DataSourceProfile, consumerScope, cancellationToken)
            ?? throw Error("data_source_profile_not_found", $"Data Source Profile '{reference.Name}' was not found.");
        if (!profile.Value.Definition.Enabled)
            throw Error("data_source_profile_disabled", $"Data Source Profile '{profile.Value.Address}' is disabled.");
        var version = profile.Value.ActiveVersion
            ?? throw Error("data_source_profile_not_published", $"Data Source Profile '{profile.Value.Address}' has no active revision.");
        var revision = await GetRevisionCoreAsync(profile.Value, version, cancellationToken)
            ?? throw Error("data_source_profile_revision_not_found",
                $"Active profile revision '{profile.Value.Address}:{version}' was not found.");
        return new()
        {
            Name = profile.Value.Name,
            Namespace = profile.Value.Namespace,
            ScopeRef = RequireScope(profile.Value),
            Uid = profile.Value.Uid,
            Generation = profile.Value.Generation,
            Version = revision.Value.Version,
            DefinitionHash = revision.Value.DefinitionHash,
            Definition = revision.Value.Definition
        };
    }

    public async Task DeleteAsync(ResourceNamespace @namespace, string name, ResourceScopeRef? scopeRef,
        string? ifMatch, CancellationToken cancellationToken)
    {
        var profile = await GetAsync(@namespace, name, scopeRef, cancellationToken) ?? throw NotFound(name, @namespace);
        EnsureProtectedMutationAllowed(profile.Value);
        var exact = RequireScope(profile.Value);
        var usages = (await store.ListAllAsync<DataSourceResource>(DataSourceResourceKinds.DataSource, cancellationToken))
            .Where(value => value.Value.Definition.Profile.Name == profile.Value.Name
                && (value.Value.Definition.Profile.Namespace ?? value.Value.Namespace) == profile.Value.Namespace
                && (value.Value.Definition.Profile.ScopeRef is null || value.Value.Definition.Profile.ScopeRef == exact))
            .ToArray();
        if (usages.Length != 0)
            throw Error("data_source_profile_in_use", $"Data Source Profile '{profile.Value.Address}' is referenced by {usages.Length} Data Source(s).");
        var revisions = await ListRevisionsAsync(@namespace, name, exact, cancellationToken);
        await scopeOperations.WriteAsync(profile.Value, exact, AuthorizationPermissions.ResourcesDelete, async token =>
        {
            foreach (var revision in revisions)
                await store.DeleteExactAsync(Address(exact, @namespace, revision.Value.Kind, revision.Value.Name), revision.ETag, token);
            await store.DeleteExactAsync(Address(exact, @namespace, profile.Value.Kind, name), ifMatch, token);
            return true;
        }, cancellationToken);
        await AuditAsync(SecurityAuditActions.DataSourceProfileDeleted, exact, name, cancellationToken);
    }

    public static void Validate(DataSourceProfileResource resource)
    {
        if (resource.Kind != DataSourceResourceKinds.DataSourceProfile || resource.ApiVersion != ResourceApiVersions.CoreV1)
            throw Error("data_source_profile_identity_invalid", "Invalid Data Source Profile resource envelope.");
        if (resource.ScopeRef is null) throw Error("data_source_profile_scope_required", "A Data Source Profile requires an ownership scope.");
        ResourceScopePolicy.EnsureAllowed(resource, resource.ScopeRef.Value);
        ValidateToken(resource.Name, "profile name");
        if (string.IsNullOrWhiteSpace(resource.Definition?.DisplayName) || resource.Definition.DisplayName.Length > 200)
            throw Error("data_source_profile_display_name_invalid", "Profile display names must contain 1 to 200 characters.");
        if (resource.Definition.Description?.Length > 2000)
            throw Error("data_source_profile_description_too_long", "Profile descriptions cannot exceed 2000 characters.");
        ValidateVersion(resource.Definition.Version);
        ValidateSchema(resource.Definition.ConfigurationSchema);
        ValidateFlow(resource.Definition.AcquisitionFlow);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in resource.Definition.ToolBindings)
        {
            ValidateToken(binding.Name, "Tool binding name");
            ValidateToken(binding.Capability, "Tool binding capability");
            if (!names.Add(binding.Name)) throw Error("data_source_profile_binding_duplicate", $"Binding '{binding.Name}' is duplicated.");
            if (binding.RequiredProvider is { } provider) ValidateToken(provider.Name, "required provider name");
        }
    }

    public static IReadOnlyList<string> ValidateConfiguration(JsonElement schema, JsonElement value) =>
        JsonSchemaSubset.Validate(schema, value);

    private async Task<StoredResource<DataSourceProfileRevisionResource>?> GetRevisionCoreAsync(
        DataSourceProfileResource profile,
        string version,
        CancellationToken cancellationToken) =>
        await store.GetExactAsync<DataSourceProfileRevisionResource>(Address(RequireScope(profile), profile.Namespace,
            DataSourceResourceKinds.DataSourceProfileRevision, RevisionName(profile.Name, version)), cancellationToken);

    private Task AuditAsync(string action, ResourceScopeRef scope, string reason, CancellationToken cancellationToken)
    {
        var current = requestContext.IsInitialized ? requestContext.Current : null;
        return audit.WriteAsync(new(action, ActorPrincipalId: current?.PrincipalId, TenantId: current?.TenantId,
            WorkspaceId: scope.Kind == ResourceScopeKind.Workspace ? scope.TargetId : current?.WorkspaceId,
            ReasonCode: reason.Length <= 64 ? reason : reason[..64]), cancellationToken);
    }

    private void EnsureProtectedMutationAllowed(DataSourceProfileResource resource)
    {
        if (IsBuiltIn(resource) && requestContext.AccessMode != ControlPlaneAccessMode.System)
            throw Error("data_source_profile_builtin_protected",
                $"Data Source Profile '{resource.Address}' is managed by Agentstration and cannot be modified directly.");
    }

    private static bool IsBuiltIn(Resource resource) =>
        resource.Metadata.Annotations.TryGetValue(ResourceProvenanceAnnotations.BuiltIn, out var builtIn)
        && string.Equals(builtIn, "true", StringComparison.OrdinalIgnoreCase);

    private static void ValidateSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(schema.GetRawText()) > MaximumConfigurationSchemaBytes)
            throw Error("data_source_profile_schema_invalid", "Configuration schemas must be bounded JSON objects.");
    }

    private static void ValidateFlow(DataSourceFlowTarget target)
    {
        ValidateToken(target.Name, "acquisition Flow name");
        if (target.UseActiveVersion == (target.Version is not null))
            throw Error("data_source_profile_flow_reference_invalid", "Acquisition Flow references must select either the active version or one exact version.");
    }

    private static void ValidateToken(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128
            || value.Any(character => !char.IsLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
            throw Error("data_source_profile_token_invalid", $"The {label} contains unsupported characters or is too long.");
    }

    private static void ValidateVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64
            || value.Any(character => !char.IsLetterOrDigit(character) && character is not '.' and not '-'))
            throw Error("data_source_profile_version_invalid", "Profile versions contain unsupported characters or are too long.");
    }

    private static string Hash(DataSourceProfileProperties definition) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(definition)));
    private static string RevisionName(string name, string version) =>
        $"data-source-profile-revision-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{name}\0{version}")))[..32]}";
    private static ResourceScopeRef RequireScope(Resource resource) => resource.ScopeRef
        ?? throw Error("data_source_scope_required", $"Resource '{resource.Address}' requires an ownership scope.");
    private static ScopedResourceAddress Address(ResourceScopeRef scope, ResourceNamespace ns, string kind, string name) =>
        ScopedResourceAddress.Create(scope, ns, kind, name);
    private static ResourceNotFoundException NotFound(string name, ResourceNamespace ns) =>
        new(new ResourceKey(DataSourceResourceKinds.DataSourceProfile, name, ns));
    private static DataSourceValidationException Error(string code, string message) => new(code, message);
    private static ResourceStatus Ready(string reason, string message) => new()
    {
        ProvisioningState = ProvisioningState.Succeeded,
        Conditions = [new ResourceCondition { Type = "Ready", Status = "True", Reason = reason, Message = message }]
    };
}

public sealed class DataSourceManagementService(
    IResourceStore store,
    IResourceScopeOperations scopeOperations,
    DataSourceProfileService profiles,
    ICurrentRequestContext requestContext,
    ISecurityAuditWriter audit)
{
    public const int MaximumConfigurationBytes = 64 * 1024;

    public Task<IReadOnlyList<StoredResource<DataSourceResource>>> ListAsync(CancellationToken cancellationToken) =>
        store.ListAllAsync<DataSourceResource>(DataSourceResourceKinds.DataSource, cancellationToken);

    public Task<StoredResource<DataSourceResource>?> GetAsync(ResourceNamespace ns, string name,
        ResourceScopeRef? scopeRef, CancellationToken cancellationToken) => scopeRef is { } exact
            ? store.GetExactAsync<DataSourceResource>(ScopedResourceAddress.Create(exact, ns,
                DataSourceResourceKinds.DataSource, name), cancellationToken)
            : store.GetAsync<DataSourceResource>(new(DataSourceResourceKinds.DataSource, name, ns), cancellationToken);

    public async Task<StoredResource<DataSourceResource>> CreateAsync(DataSourceResource resource, CancellationToken cancellationToken)
    {
        var scope = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(DataSourceResourceKinds.DataSource);
        var desired = resource with { ScopeRef = scope, Generation = 1 };
        var readiness = await ValidateAndResolveAsync(desired, cancellationToken);
        desired = desired with { Status = Status(readiness) };
        var stored = await scopeOperations.WriteAsync(desired, scope, AuthorizationPermissions.ResourcesWrite,
            token => store.PutExactAsync(scope, desired, null, true, token), cancellationToken);
        await AuditAsync(SecurityAuditActions.DataSourceCreated, scope, stored.Value.Name, cancellationToken);
        return stored;
    }

    public async Task<StoredResource<DataSourceResource>> PutAsync(ResourceNamespace ns, string name,
        ResourceScopeRef? scopeRef, DataSourceProperties definition, string? ifMatch, CancellationToken cancellationToken)
    {
        var existing = await GetAsync(ns, name, scopeRef, cancellationToken) ?? throw NotFound(name, ns);
        var scope = existing.Value.ScopeRef ?? throw Error("data_source_scope_required", "The Data Source has no ownership scope.");
        var updated = existing.Value with { Generation = checked(existing.Value.Generation + 1), Definition = definition };
        var readiness = await ValidateAndResolveAsync(updated, cancellationToken);
        updated = updated with { Status = Status(readiness) };
        var stored = await scopeOperations.WriteAsync(updated, scope, AuthorizationPermissions.ResourcesWrite,
            token => store.PutExactAsync(scope, updated, ifMatch, false, token), cancellationToken);
        await AuditAsync(SecurityAuditActions.DataSourceUpdated, scope, stored.Value.Name, cancellationToken);
        return stored;
    }

    public async Task<DataSourceReadiness> GetReadinessAsync(ResourceNamespace ns, string name,
        ResourceScopeRef? scopeRef, CancellationToken cancellationToken)
    {
        var source = await GetAsync(ns, name, scopeRef, cancellationToken) ?? throw NotFound(name, ns);
        return await ResolveReadinessAsync(source.Value, cancellationToken);
    }

    public async Task DeleteAsync(ResourceNamespace ns, string name, ResourceScopeRef? scopeRef,
        string? ifMatch, CancellationToken cancellationToken)
    {
        var source = await GetAsync(ns, name, scopeRef, cancellationToken) ?? throw NotFound(name, ns);
        var exact = source.Value.ScopeRef ?? throw Error("data_source_scope_required", "The Data Source has no ownership scope.");
        await scopeOperations.WriteAsync(source.Value, exact, AuthorizationPermissions.ResourcesDelete, async token =>
        {
            await store.DeleteExactAsync(ScopedResourceAddress.Create(exact, ns, source.Value.Kind, name), ifMatch, token);
            return true;
        }, cancellationToken);
        await AuditAsync(SecurityAuditActions.DataSourceDeleted, exact, name, cancellationToken);
    }

    private async Task<DataSourceReadiness> ValidateAndResolveAsync(DataSourceResource resource, CancellationToken cancellationToken)
    {
        Validate(resource);
        var readiness = await ResolveReadinessAsync(resource, cancellationToken);
        if (!readiness.Ready) throw Error("data_source_not_ready", string.Join(' ', readiness.Issues));
        return readiness;
    }

    private async Task<DataSourceReadiness> ResolveReadinessAsync(DataSourceResource resource, CancellationToken cancellationToken)
    {
        if (!resource.Definition.Enabled) return new(false, false, null, ["The Data Source is disabled."]);
        try
        {
            var profile = await profiles.ResolveActiveAsync(resource.ScopeRef!.Value, resource.Namespace,
                resource.Definition.Profile, cancellationToken);
            var issues = DataSourceProfileService.ValidateConfiguration(profile.Definition.ConfigurationSchema,
                resource.Definition.Configuration);
            return new(issues.Count == 0, true, profile, issues);
        }
        catch (Exception exception) when (exception is DataSourceValidationException
            or ResourceReferenceAmbiguousException or ResourceReferenceOutsideScopeException)
        {
            return new(false, true, null, [exception.Message]);
        }
    }

    public static void Validate(DataSourceResource resource)
    {
        if (resource.Kind != DataSourceResourceKinds.DataSource || resource.ApiVersion != ResourceApiVersions.CoreV1)
            throw Error("data_source_identity_invalid", "Invalid Data Source resource envelope.");
        if (resource.ScopeRef is null) throw Error("data_source_scope_required", "A Data Source requires an ownership scope.");
        ResourceScopePolicy.EnsureAllowed(resource, resource.ScopeRef.Value);
        if (string.IsNullOrWhiteSpace(resource.Name) || string.IsNullOrWhiteSpace(resource.Definition?.DisplayName))
            throw Error("data_source_identity_invalid", "Data Source name and display name are required.");
        if (resource.Definition.DisplayName.Length > 200 || resource.Definition.Description?.Length > 2000)
            throw Error("data_source_metadata_invalid", "Data Source display name or description is too long.");
        if (resource.Definition.Configuration.ValueKind != JsonValueKind.Object
            || Encoding.UTF8.GetByteCount(resource.Definition.Configuration.GetRawText()) > MaximumConfigurationBytes)
            throw Error("data_source_configuration_invalid", "Data Source configuration must be a bounded JSON object.");
    }

    private Task AuditAsync(string action, ResourceScopeRef scope, string reason, CancellationToken cancellationToken)
    {
        var current = requestContext.IsInitialized ? requestContext.Current : null;
        return audit.WriteAsync(new(action, ActorPrincipalId: current?.PrincipalId, TenantId: current?.TenantId,
            WorkspaceId: scope.Kind == ResourceScopeKind.Workspace ? scope.TargetId : current?.WorkspaceId,
            ReasonCode: reason), cancellationToken);
    }

    private static ResourceStatus Status(DataSourceReadiness readiness) => new()
    {
        ProvisioningState = readiness.Ready ? ProvisioningState.Succeeded : ProvisioningState.Failed,
        Conditions = [new ResourceCondition
        {
            Type = "Ready",
            Status = readiness.Ready ? "True" : "False",
            Reason = readiness.Ready ? "ProfileResolved" : "ProfileUnavailable",
            Message = readiness.Ready ? "The Data Source is ready." : string.Join(' ', readiness.Issues)
        }]
    };
    private static ResourceNotFoundException NotFound(string name, ResourceNamespace ns) =>
        new(new ResourceKey(DataSourceResourceKinds.DataSource, name, ns));
    private static DataSourceValidationException Error(string code, string message) => new(code, message);
}

internal static class JsonSchemaSubset
{
    public static IReadOnlyList<string> Validate(JsonElement schema, JsonElement value)
    {
        var issues = new List<string>();
        ValidateNode(schema, value, "$", issues);
        return issues;
    }

    private static void ValidateNode(JsonElement schema, JsonElement value, string path, ICollection<string> issues)
    {
        if (schema.ValueKind != JsonValueKind.Object) return;
        if (schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
            && !Matches(type.GetString(), value))
        {
            issues.Add($"{path} must be of type '{type.GetString()}'.");
            return;
        }
        if (value.ValueKind != JsonValueKind.Object) return;
        var properties = schema.TryGetProperty("properties", out var propertySchema)
            && propertySchema.ValueKind == JsonValueKind.Object ? propertySchema : default;
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            foreach (var item in required.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String))
                if (!value.TryGetProperty(item.GetString()!, out _)) issues.Add($"{path}.{item.GetString()} is required.");
        foreach (var property in value.EnumerateObject())
        {
            if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var child))
                ValidateNode(child, property.Value, $"{path}.{property.Name}", issues);
            else if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False)
                issues.Add($"{path}.{property.Name} is not allowed.");
        }
    }

    private static bool Matches(string? type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true
    };
}
