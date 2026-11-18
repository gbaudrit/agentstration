using System.Security.Cryptography;
using System.Text.Json;

namespace Agentstration.Storage.Contracts;

public static class StorageDigests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Schema(JsonElement schema) => Compute(schema);
    public static string Version(StorageTypeVersionDescriptor version)
    {
        var definition = Compute(new
        {
            version.Version,
            version.SchemaDigest,
            version.ConfigurationSchema,
            version.EnvelopeSchemaDigest,
            version.EnvelopeSchema,
            version.RequiresConnectedService,
            version.Requirements,
            version.Capabilities,
            version.Limits,
            version.Contribution,
            version.Deprecated
        });
        // Preserve the exact published digest of versions with no ASP mapping.
        var mapped = version.Asp is null ? definition : Compute(new { Definition = definition, version.Asp });
        return version.Documents is null ? mapped : Compute(new { Definition = mapped, version.Documents });
    }
    public static string Binding(StorageProperties definition)
    {
        var result = Compute(new
        {
            definition.Type,
            definition.ConnectedService,
            definition.ConnectedServiceType,
            Bindings = definition.Bindings.OrderBy(binding => binding.RequirementId, StringComparer.Ordinal),
            definition.Configuration,
            definition.Envelope
        });
        return definition.Collections.Count == 0 ? result : Compute(new { Binding = result, definition.Collections });
    }

    private static string Compute<T>(T value) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Options))).ToLowerInvariant()}";
}
