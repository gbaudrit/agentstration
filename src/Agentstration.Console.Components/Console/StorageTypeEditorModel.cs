using System.Text.Json;
using Agentstration.Storage.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

/// <summary>Draft of a new immutable contract, with no provider-contribution input.</summary>
public sealed class StorageTypeVersionDraft
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };
    public string Version { get; set; } = "1.0.0";
    public string ConfigurationSchema { get; set; } = "{\"type\":\"object\",\"additionalProperties\":false}";
    public string EnvelopeSchema { get; set; } = "{\"type\":\"object\",\"additionalProperties\":false}";
    public string Requirements { get; set; } = "[]";
    public bool RequiresConnectedService { get; set; }
    public bool List { get; set; }
    public bool Stat { get; set; }
    public bool Read { get; set; }
    public bool Write { get; set; }
    public bool Delete { get; set; }
    public bool StableVersions { get; set; }
    public bool DurableRetention { get; set; }
    public bool Deprecated { get; set; }
    public int MaximumPageSize { get; set; } = 100;
    public int MaximumReadBytes { get; set; } = 10485760;
    public int MaximumWriteBytes { get; set; } = 10485760;

    public IReadOnlyList<string> InvalidFields()
    {
        var fields = new List<string>();
        var version = Version.Trim();
        if (version.Length is < 1 or > 64 || !version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')) fields.Add(nameof(Version));
        if (!ValidJson(ConfigurationSchema, 262144, false)) fields.Add(nameof(ConfigurationSchema));
        if (!ValidJson(EnvelopeSchema, 262144, false)) fields.Add(nameof(EnvelopeSchema));
        if (!ValidJson(Requirements, 65536, true)) fields.Add(nameof(Requirements));
        if (MaximumPageSize <= 0) fields.Add(nameof(MaximumPageSize));
        if (MaximumReadBytes <= 0) fields.Add(nameof(MaximumReadBytes));
        if (MaximumWriteBytes <= 0) fields.Add(nameof(MaximumWriteBytes));
        return fields;
    }
    private static bool ValidJson(string source, int maximum, bool requirements)
    {
        if (source.Length > maximum) return false;
        try
        {
            return requirements ? JsonSerializer.Deserialize<StorageRequirementDescriptor[]>(source, JsonOptions) is not null
                : JsonSerializer.Deserialize<JsonElement>(source, JsonOptions).ValueKind == JsonValueKind.Object;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException) { return false; }
    }

    public StorageTypeVersionDescriptor Build()
    {
        if (ConfigurationSchema.Length > 262144 || EnvelopeSchema.Length > 262144 || Requirements.Length > 65536)
            throw new JsonException();
        var configuration = JsonSerializer.Deserialize<JsonElement>(ConfigurationSchema, JsonOptions);
        var envelope = JsonSerializer.Deserialize<JsonElement>(EnvelopeSchema, JsonOptions);
        if (configuration.ValueKind != JsonValueKind.Object || envelope.ValueKind != JsonValueKind.Object)
            throw new JsonException();
        return new()
        {
            Version = Version.Trim(),
            ConfigurationSchema = configuration,
            SchemaDigest = StorageDigests.Schema(configuration),
            EnvelopeSchema = envelope,
            EnvelopeSchemaDigest = StorageDigests.Schema(envelope),
            RequiresConnectedService = RequiresConnectedService,
            Requirements = JsonSerializer.Deserialize<StorageRequirementDescriptor[]>(Requirements, JsonOptions) ?? throw new JsonException(),
            Capabilities = new()
            {
                List = List,
                Stat = Stat,
                Read = Read,
                Write = Write,
                Delete = Delete,
                StableVersions = StableVersions,
                DurableRetention = DurableRetention
            },
            Limits = new(MaximumPageSize, MaximumReadBytes, MaximumWriteBytes),
            Deprecated = Deprecated
        };
    }
}

public static class StorageTypeLinks
{
    public static string Details(StorageTypeResource resource) =>
        $"/storage-types/{Uri.EscapeDataString(resource.Name)}?scopeRef={Uri.EscapeDataString(resource.ScopeRef!.Value.Value)}&namespace={Uri.EscapeDataString(resource.Namespace.Value)}";
    public static string Action(StorageTypeResource resource, string action) =>
        Details(resource).Replace("?", $"/{action}?", StringComparison.Ordinal);
}


public enum StorageTypeIdentityIssue { TechnicalName, DisplayName, Description, Namespace }

/// <summary>Family draft and the pre-existing identity rules; commands and localization remain with the screen.</summary>
public sealed class StorageTypeEditorModel
{
    public string Name { get; set; } = string.Empty;
    public string Namespace { get; set; } = ResourceNamespace.DefaultValue;
    public ResourceScopeRef? ScopeRef { get; set; }
    public string Tags { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Enabled { get; set; } = true;
    public string PreferredVersion { get; set; } = string.Empty;
    public bool SetPreferred { get; set; }
    public StorageTypeVersionDraft Version { get; } = new();

    public IReadOnlyDictionary<string, StorageTypeIdentityIssue> IdentityIssues()
    {
        var issues = new Dictionary<string, StorageTypeIdentityIssue>(StringComparer.Ordinal);
        if (Name.Length is < 1 or > 128 || !char.IsAsciiLetterOrDigit(Name.FirstOrDefault()) || Name.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c is not '.' and not '-')) issues[nameof(Name)] = StorageTypeIdentityIssue.TechnicalName;
        if (string.IsNullOrWhiteSpace(DisplayName) || DisplayName.Length > 200) issues[nameof(DisplayName)] = StorageTypeIdentityIssue.DisplayName;
        if (Description?.Length > 2000) issues[nameof(Description)] = StorageTypeIdentityIssue.Description;
        try { _ = ResourceNamespace.Parse(Namespace); }
        catch (ArgumentException) { issues[nameof(Namespace)] = StorageTypeIdentityIssue.Namespace; }
        return issues;
    }
    public static StorageTypeEditorModel From(StorageTypeResource resource) => new()
    {
        Name = resource.Name, Namespace = resource.Namespace.Value, ScopeRef = resource.ScopeRef,
        Tags = string.Join(Environment.NewLine, resource.Metadata.Tags.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}")),
        DisplayName = resource.Definition.DisplayName, Description = resource.Definition.Description,
        Enabled = resource.Definition.Enabled, PreferredVersion = resource.Definition.PreferredVersion
    };
}
