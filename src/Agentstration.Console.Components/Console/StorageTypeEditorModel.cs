using System.Text.Json;
using Agentstration.Storage.Contracts;

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
    public int MaximumReadBytes { get; set; } = 1048576;
    public int MaximumWriteBytes { get; set; } = 1048576;

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
