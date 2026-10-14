using System.Text.Json;

namespace Agentstration.Schemas;

[Flags]
public enum SchemaFormatCapabilities
{
    None = 0,
    ValidateDefinition = 1,
    ValidateInstance = 2,
    VisualAuthoring = 4,
    InstancePreview = 8
}

public sealed record SchemaFormatProviderIdentity(string Id, string Version);

public sealed record SchemaFormatDescriptor(
    string Format,
    string DisplayName,
    SchemaFormatProviderIdentity Provider,
    IReadOnlyList<string> Dialects,
    SchemaFormatCapabilities Capabilities,
    bool IsDefault,
    bool IsAvailable = true,
    string? UnavailableReason = null);

public sealed record SchemaFormatValidationIssue(string Path, string Code, string Message);

public sealed record SchemaFormatValidationResult(IReadOnlyList<SchemaFormatValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;

    public static SchemaFormatValidationResult Valid { get; } = new([]);
}

public interface ISchemaFormatHandler
{
    SchemaFormatDescriptor Descriptor { get; }

    ValueTask<SchemaFormatValidationResult> ValidateDefinitionAsync(
        JsonElement definition,
        string? dialect,
        CancellationToken cancellationToken);

    ValueTask<SchemaFormatValidationResult> ValidateInstanceAsync(
        JsonElement definition,
        JsonElement instance,
        string? dialect,
        CancellationToken cancellationToken);
}

public interface ISchemaFormatRegistry
{
    IReadOnlyList<SchemaFormatDescriptor> List();

    ISchemaFormatHandler Resolve(
        string format,
        SchemaFormatCapabilities capability,
        SchemaFormatProviderIdentity? provider = null);
}

public sealed class SchemaFormatResolutionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class SchemaFormatRegistry(IEnumerable<ISchemaFormatHandler> handlers) : ISchemaFormatRegistry
{
    private readonly IReadOnlyList<ISchemaFormatHandler> handlers = handlers
        .OrderBy(value => value.Descriptor.Format, StringComparer.Ordinal)
        .ThenBy(value => value.Descriptor.Provider.Id, StringComparer.Ordinal)
        .ThenBy(value => value.Descriptor.Provider.Version, StringComparer.Ordinal)
        .ToArray();

    public IReadOnlyList<SchemaFormatDescriptor> List() => handlers.Select(value => value.Descriptor).ToArray();

    public ISchemaFormatHandler Resolve(string format, SchemaFormatCapabilities capability,
        SchemaFormatProviderIdentity? provider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        var candidates = handlers.Where(value => string.Equals(value.Descriptor.Format, format, StringComparison.Ordinal))
            .Where(value => provider is null || value.Descriptor.Provider == provider)
            .ToArray();
        if (candidates.Length == 0)
            throw new SchemaFormatResolutionException("schema_format_unavailable",
                provider is null
                    ? $"Schema format '{format}' is not available."
                    : $"Schema format '{format}' provider '{provider.Id}@{provider.Version}' is not available.");

        var capable = candidates.Where(value => value.Descriptor.IsAvailable
            && value.Descriptor.Capabilities.HasFlag(capability)).ToArray();
        if (capable.Length == 0)
        {
            var unavailable = candidates.FirstOrDefault(value => !value.Descriptor.IsAvailable);
            throw new SchemaFormatResolutionException(
                unavailable is null ? "schema_format_capability_unavailable" : "schema_format_provider_unavailable",
                unavailable?.Descriptor.UnavailableReason
                    ?? $"Schema format '{format}' does not provide capability '{capability}'.");
        }

        if (provider is not null) return Single(capable, format, capability);
        var defaults = capable.Where(value => value.Descriptor.IsDefault).ToArray();
        if (defaults.Length == 1) return defaults[0];
        if (capable.Length == 1 && defaults.Length == 0) return capable[0];
        throw new SchemaFormatResolutionException("schema_format_provider_ambiguous",
            $"Schema format '{format}' has multiple providers for capability '{capability}'; select a provider explicitly.");
    }

    private static ISchemaFormatHandler Single(IReadOnlyList<ISchemaFormatHandler> candidates, string format,
        SchemaFormatCapabilities capability)
    {
        if (candidates.Count == 1) return candidates[0];
        throw new SchemaFormatResolutionException("schema_format_provider_ambiguous",
            $"Schema format '{format}' provider identity is duplicated for capability '{capability}'.");
    }
}
