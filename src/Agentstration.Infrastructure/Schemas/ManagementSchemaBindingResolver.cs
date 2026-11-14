using System.Text.Json;
using Agentstration.ResourceManagement;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;

namespace Agentstration.Infrastructure.Schemas;

public sealed class ManagementSchemaBindingResolver(
    SchemaManagementService schemas,
    DescendantResourceUseAuthorizer useAuthorizer,
    ISchemaFormatRegistry formats) : ISchemaBindingResolver
{
    public async ValueTask<SchemaBindingSnapshot> ResolveAsync(
        string path,
        SchemaBinding binding,
        SchemaBindingResolutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(binding);
        var forms = (binding.Inline is null ? 0 : 1) + (binding.Reference is null ? 0 : 1);
        if (forms != 1)
            throw Error("schema_binding_ambiguous", "A Schema binding must contain exactly one inline definition or version reference.");

        if (binding.Inline is { } inline)
        {
            await ValidateAsync(inline.Format, inline.Dialect, inline.Content, context.RequiredFormat, cancellationToken);
            return new(path, inline.Format, inline.Dialect, inline.Content.Clone(),
                JsonSchemaSelection.DefinitionHash(inline.Format, inline.Dialect, inline.Content));
        }

        var reference = binding.Reference!;
        if (string.IsNullOrWhiteSpace(reference.Name) || string.IsNullOrWhiteSpace(reference.Version))
            throw Error("schema_reference_invalid", "A Schema reference requires a name and an exact published version.");
        var ownerScope = reference.ScopeRef ?? context.ConsumerScopeRef;
        var @namespace = reference.Namespace ?? context.OwnerNamespace;
        if (!await useAuthorizer.IsSameOrDescendantAsync(ownerScope, context.ConsumerScopeRef,
                cancellationToken))
            throw Error("schema_reference_forbidden", "The referenced Schema is not available to the consuming scope.");
        var source = await schemas.GetExactAsync(ownerScope, @namespace, reference.Name, cancellationToken)
            ?? throw Error("schema_reference_not_found", $"Referenced Schema '{@namespace}/{reference.Name}' was not found.");
        if (!await useAuthorizer.CanUseAsync(ownerScope, context.ConsumerScopeRef,
                source.Value.Definition.UsePolicy, cancellationToken))
            throw Error("schema_reference_forbidden", "The referenced Schema is not available to the consuming scope.");
        var version = await schemas.GetVersionAsync(ownerScope, @namespace, reference.Name,
                reference.Version, cancellationToken)
            ?? throw Error("schema_version_not_found",
                $"Referenced Schema version '{@namespace}/{reference.Name}:{reference.Version}' was not found.");

        if (version.Value.DefinitionHash != JsonSchemaSelection.DefinitionHash(version.Value.Format,
                version.Value.Dialect, version.Value.Content, published: true))
            throw Error("schema_version_hash_mismatch", "The published Schema definition does not match its hash.");
        if (version.Value.SchemaUid != source.Value.Uid)
            throw Error("schema_reference_replaced", "The published version belongs to a replaced Schema.");
        if (!string.IsNullOrEmpty(reference.SchemaPath) && version.Value.Dialect is { } dialect
            && dialect != BuiltinJsonSchemaFormatHandler.Draft202012)
            throw Error("schema_selection_dialect_unsupported", "Sub-schema selection requires the supported JSON Schema dialect.");
        JsonElement selected;
        try
        {
            selected = reference.SchemaPath is null or "" ? version.Value.Content.Clone()
                : version.Value.Format == SchemaFormatIds.JsonSchema
                    ? JsonSchemaSelection.Select(version.Value.Content, reference.SchemaPath)
                    : throw Error("schema_selection_format_unsupported", "Sub-schema selection requires JSON Schema.");
        }
        catch (SchemaSelectionException exception) { throw Error(exception.Code, "The selected sub-schema cannot be resolved."); }
        await ValidateAsync(version.Value.Format, version.Value.Dialect, selected, context.RequiredFormat, cancellationToken);
        return new(path, version.Value.Format, version.Value.Dialect, selected,
            reference.SchemaPath is null or "" ? version.Value.DefinitionHash
                : JsonSchemaSelection.DefinitionHash(version.Value.Format, version.Value.Dialect, selected),
            reference with { ScopeRef = ownerScope, Namespace = @namespace },
            version.Value.SchemaUid, version.Value.SchemaGeneration)
        { ParentDefinitionHash = version.Value.DefinitionHash };
    }

    private async ValueTask ValidateAsync(string format, string? dialect, JsonElement content,
        string requiredFormat, CancellationToken cancellationToken)
    {
        if (!string.Equals(format, requiredFormat, StringComparison.Ordinal))
            throw Error("schema_format_incompatible",
                $"Schema format '{format}' is incompatible with the required format '{requiredFormat}'.");
        if (content.ValueKind == JsonValueKind.Undefined)
            throw Error("schema_content_required", "A Schema binding requires content.");
        ISchemaFormatHandler handler;
        try
        {
            handler = await formats.ResolveAsync(format, SchemaFormatCapabilities.ValidateDefinition,
                cancellationToken: cancellationToken);
        }
        catch (SchemaFormatResolutionException exception)
        {
            throw Error(exception.Code, exception.Message);
        }
        var validation = await handler.ValidateDefinitionAsync(content, dialect, cancellationToken);
        if (!validation.IsValid)
            throw Error("schema_definition_invalid",
                $"Schema definition is invalid at '{validation.Issues[0].Path}': {validation.Issues[0].Message}");
    }

    private static SchemaBindingResolutionException Error(string code, string message) => new(code, message);
}
