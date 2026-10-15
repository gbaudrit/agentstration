using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Security.Contracts;

namespace Agentstration.Schemas;

public sealed class SchemaManagementService(
    IResourceStore store,
    IResourceScopeOperations scopeOperations,
    DescendantResourceUseAuthorizer useAuthorizer,
    ISchemaFormatRegistry formatRegistry,
    ISecurityAuditWriter audit,
    TimeProvider timeProvider)
{
    public const int MaximumContentBytes = 1_048_576;

    public Task<IReadOnlyList<StoredResource<SchemaResource>>> ListAsync(CancellationToken cancellationToken) =>
        store.ListAllAsync<SchemaResource>(SchemaResourceKinds.Schema, cancellationToken);

    public Task<StoredResource<SchemaResource>?> GetAsync(ResourceNamespace @namespace, string name,
        CancellationToken cancellationToken) =>
        store.GetAsync<SchemaResource>(new(SchemaResourceKinds.Schema, name, @namespace), cancellationToken);

    public Task<StoredResource<SchemaResource>?> GetExactAsync(ResourceScopeRef scopeRef, ResourceNamespace @namespace,
        string name, CancellationToken cancellationToken) =>
        store.GetExactAsync<SchemaResource>(Scoped(scopeRef, @namespace, SchemaResourceKinds.Schema, name), cancellationToken);

    public async Task<IReadOnlyList<StoredResource<SchemaVersionResource>>> ListVersionsAsync(
        ResourceScopeRef scopeRef, ResourceNamespace @namespace, string name, CancellationToken cancellationToken) =>
        (await store.ListExactAsync<SchemaVersionResource>(scopeRef, SchemaResourceKinds.SchemaVersion,
            0, 1000, cancellationToken))
        .Where(value => value.Value.SchemaName == name && value.Value.SchemaNamespace == @namespace)
        .OrderBy(value => value.Value.Version, StringComparer.Ordinal)
        .ToArray();

    public async Task<StoredResource<SchemaVersionResource>?> GetVersionAsync(ResourceScopeRef scopeRef,
        ResourceNamespace @namespace, string name, string version, CancellationToken cancellationToken)
    {
        var stored = await store.GetExactAsync<SchemaVersionResource>(Scoped(scopeRef, @namespace,
            SchemaResourceKinds.SchemaVersion, VersionResourceName(name, version)), cancellationToken);
        return stored is not null
            && stored.Value.SchemaName == name
            && stored.Value.SchemaNamespace == @namespace
            && stored.Value.Version == version
                ? stored
                : null;
    }

    public async Task<StoredResource<SchemaResource>> CreateAsync(SchemaResource resource,
        CancellationToken cancellationToken)
    {
        var scopeRef = resource.ScopeRef ?? scopeOperations.DefaultScopeRef(SchemaResourceKinds.Schema);
        var desired = resource with { ScopeRef = scopeRef, Generation = 1, Status = Succeeded() };
        await ValidateAsync(desired, cancellationToken);
        ResourceScopePolicy.EnsureAllowed(desired, scopeRef);
        var stored = await scopeOperations.WriteAsync(desired, scopeRef, AuthorizationPermissions.ResourcesWrite,
            token => store.PutExactAsync(scopeRef, desired, null, true, token), cancellationToken);
        await AuditAsync(SecurityAuditActions.SchemaCreated, scopeRef, cancellationToken);
        return stored;
    }

    public async Task<StoredResource<SchemaResource>> PutAsync(ResourceScopeRef scopeRef,
        ResourceNamespace @namespace, string name, SchemaProperties definition, string? etag,
        CancellationToken cancellationToken)
    {
        var existing = await GetExactAsync(scopeRef, @namespace, name, cancellationToken)
            ?? throw new SchemaNotFoundException(@namespace, name);
        if (!string.Equals(existing.Value.Definition.Format, definition.Format, StringComparison.Ordinal)
            || !string.Equals(existing.Value.Definition.Dialect, definition.Dialect, StringComparison.Ordinal))
            throw new SchemaValidationException("schema_format_immutable",
                "A Schema format and dialect cannot change after the resource is created.");
        var desired = existing.Value with
        {
            Definition = definition,
            Generation = checked(existing.Value.Generation + 1),
            Status = Succeeded()
        };
        await ValidateAsync(desired, cancellationToken);
        var stored = await scopeOperations.WriteAsync(existing.Value, scopeRef,
            AuthorizationPermissions.ResourcesWrite,
            token => store.PutExactAsync(scopeRef, desired, etag, false, token), cancellationToken);
        await AuditAsync(SecurityAuditActions.SchemaUpdated, scopeRef, cancellationToken);
        return stored;
    }

    public async Task<StoredResource<SchemaVersionResource>> PublishAsync(ResourceScopeRef scopeRef,
        ResourceNamespace @namespace, string name, string version, CancellationToken cancellationToken)
    {
        ValidateVersion(version);
        var source = await GetExactAsync(scopeRef, @namespace, name, cancellationToken)
            ?? throw new SchemaNotFoundException(@namespace, name);
        var hash = DefinitionHash(source.Value);
        if (await GetVersionAsync(scopeRef, @namespace, name, version, cancellationToken) is { } existing)
        {
            if (existing.Value.DefinitionHash == hash) return existing;
            throw new SchemaValidationException("schema_version_immutable",
                $"Published Schema version '{version}' is immutable.");
        }

        var published = new SchemaVersionResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = SchemaResourceKinds.SchemaVersion,
            Metadata = new()
            {
                Name = VersionResourceName(name, version),
                Namespace = @namespace,
                Tags = source.Value.Metadata.Tags,
                Annotations = source.Value.Metadata.Annotations
            },
            ScopeRef = scopeRef,
            SchemaUid = source.Value.Uid,
            SchemaName = name,
            SchemaNamespace = @namespace,
            SchemaGeneration = source.Value.Generation,
            Version = version,
            Format = source.Value.Definition.Format,
            Dialect = source.Value.Definition.Dialect,
            Content = source.Value.Definition.Content.Clone(),
            DefinitionHash = hash,
            PublishedAt = timeProvider.GetUtcNow(),
            Generation = 1,
            Status = Succeeded()
        };
        var stored = await scopeOperations.WriteAsync(source.Value, scopeRef,
            AuthorizationPermissions.ResourcesWrite,
            token => store.CreateImmutableAsync(published, token), cancellationToken);
        await AuditAsync(SecurityAuditActions.SchemaVersionPublished, scopeRef, cancellationToken);
        return stored;
    }

    private async Task ValidateAsync(SchemaResource resource, CancellationToken cancellationToken)
    {
        if (resource.Kind != SchemaResourceKinds.Schema || resource.ApiVersion != ResourceApiVersions.CoreV1)
            throw new SchemaValidationException("schema_identity_invalid", "Invalid Schema resource envelope.");
        ValidateName(resource.Name);
        if (string.IsNullOrWhiteSpace(resource.Definition?.DisplayName) || resource.Definition.DisplayName.Length > 200)
            throw new SchemaValidationException("schema_display_name_invalid",
                "Schema display names must contain 1 to 200 characters.");
        if (resource.Definition.Description?.Length > 2_000)
            throw new SchemaValidationException("schema_description_too_long",
                "Schema descriptions cannot exceed 2000 characters.");
        if (resource.Definition.Content.ValueKind == JsonValueKind.Undefined)
            throw new SchemaValidationException("schema_content_required", "Schema content is required.");
        if (Encoding.UTF8.GetByteCount(resource.Definition.Content.GetRawText()) > MaximumContentBytes)
            throw new SchemaValidationException("schema_content_too_large",
                $"Schema content cannot exceed {MaximumContentBytes} UTF-8 bytes.");
        ISchemaFormatHandler handler;
        try
        {
            handler = formatRegistry.Resolve(resource.Definition.Format, SchemaFormatCapabilities.ValidateDefinition);
        }
        catch (SchemaFormatResolutionException exception)
        {
            throw new SchemaValidationException(exception.Code, exception.Message);
        }
        var validation = await handler.ValidateDefinitionAsync(resource.Definition.Content,
            resource.Definition.Dialect, cancellationToken);
        if (!validation.IsValid)
            throw new SchemaValidationException("schema_definition_invalid",
                $"Schema definition is invalid at '{validation.Issues[0].Path}': {validation.Issues[0].Message}",
                validation.Issues);
        await useAuthorizer.ValidateAsync(RequireScope(resource), resource.Definition.UsePolicy, cancellationToken);
    }

    private static string DefinitionHash(SchemaResource source)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("dialect", source.Definition.Dialect);
            writer.WriteString("format", source.Definition.Format);
            writer.WritePropertyName("content");
            WriteCanonical(writer, source.Definition.Content);
            writer.WriteEndObject();
        }
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || !char.IsLetterOrDigit(name[0])
            || name.Any(character => !char.IsLower(character) && !char.IsDigit(character)
                && character is not '-' and not '.'))
            throw new SchemaValidationException("schema_name_invalid",
                "Schema names must contain 1 to 128 lowercase letters, digits, '-' or '.', and start with a letter or digit.");
    }

    private static void ValidateVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 64
            || version.Any(character => !char.IsLetterOrDigit(character) && character is not '.' and not '-'))
            throw new SchemaValidationException("schema_version_invalid",
                "Schema versions must contain only letters, digits, '.' or '-' and be at most 64 characters.");
    }

    private static string VersionResourceName(string name, string version)
    {
        var suffix = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(version)))[..16];
        return $"{name}--{suffix}";
    }

    private static ScopedResourceAddress Scoped(ResourceScopeRef scopeRef, ResourceNamespace @namespace,
        string kind, string name) => ScopedResourceAddress.Create(scopeRef, @namespace, kind, name);
    private static ResourceScopeRef RequireScope(Resource resource) => resource.ScopeRef
        ?? throw new SchemaValidationException("schema_scope_invalid", "A Schema requires an ownership scope.");
    private static ResourceStatus Succeeded() => new() { ProvisioningState = ProvisioningState.Succeeded };
    private Task AuditAsync(string action, ResourceScopeRef scopeRef, CancellationToken cancellationToken) =>
        audit.WriteAsync(new(action,
            TenantId: scopeRef.Kind == ResourceScopeKind.Tenant ? scopeRef.TargetId : null,
            WorkspaceId: scopeRef.Kind == ResourceScopeKind.Workspace ? scopeRef.TargetId : null), cancellationToken);
}
