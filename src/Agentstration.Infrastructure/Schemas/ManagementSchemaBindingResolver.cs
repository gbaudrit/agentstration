using System.Security.Cryptography;
using System.Text;
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
                DefinitionHash(inline.Format, inline.Dialect, inline.Content));
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

        await ValidateAsync(version.Value.Format, version.Value.Dialect, version.Value.Content,
            context.RequiredFormat, cancellationToken);
        return new(path, version.Value.Format, version.Value.Dialect, version.Value.Content.Clone(),
            version.Value.DefinitionHash, reference with { ScopeRef = ownerScope, Namespace = @namespace },
            version.Value.SchemaUid, version.Value.SchemaGeneration);
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

    private static string DefinitionHash(string format, string? dialect, JsonElement content)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("format", format);
            writer.WriteString("dialect", dialect);
            writer.WritePropertyName("content");
            WriteCanonical(writer, content);
            writer.WriteEndObject();
        }
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value);
            }
            writer.WriteEndObject();
            return;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
            return;
        }
        value.WriteTo(writer);
    }

    private static SchemaBindingResolutionException Error(string code, string message) => new(code, message);
}
