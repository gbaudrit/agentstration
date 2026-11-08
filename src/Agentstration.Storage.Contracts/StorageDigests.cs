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
        return version.Asp is null ? definition : Compute(new { Definition = definition, version.Asp });
    }
    public static string Binding(StorageProperties definition) => Compute(new
    {
        definition.Type,
        definition.ConnectedService,
        definition.ConnectedServiceType,
        Bindings = definition.Bindings.OrderBy(binding => binding.RequirementId, StringComparer.Ordinal),
        definition.Configuration,
        definition.Envelope
    });

    private static string Compute<T>(T value) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, Options))).ToLowerInvariant()}";
}
