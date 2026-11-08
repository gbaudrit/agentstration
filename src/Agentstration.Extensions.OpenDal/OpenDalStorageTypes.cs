using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Asp.Abstractions;

namespace Agentstration.Extensions.OpenDal;

public static class OpenDalStorageTypes
{
    public const string ExtensionId = "agentstration.extensions.opendal";
    public const string FileSystemId = "opendal.fs";
    public const string Version = "1.0.0";
    public static JsonElement ConfigurationSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object", additionalProperties = false, properties = new { }
    });
    public static JsonElement EnvelopeSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object", additionalProperties = false,
        properties = new
        {
            prefix = new { type = "string", maxLength = 256 },
            list = new { type = "boolean" }, stat = new { type = "boolean" },
            read = new { type = "boolean" }, write = new { type = "boolean" },
            maximumReadBytes = new { type = "integer", minimum = 1, maximum = 65536 },
            maximumWriteBytes = new { type = "integer", minimum = 1, maximum = 65536 },
            maximumPageSize = new { type = "integer", minimum = 1, maximum = 100 },
            keyFrom = new { type = "string", maxLength = 256 }, keyBefore = new { type = "string", maxLength = 256 }
        }
    });
    public static IReadOnlyList<AepStorageTypeContribution> Contributions { get; } =
    [
        new(FileSystemId, "OpenDAL filesystem", Version,
        [new(Version, AepSchemaDigest.Compute(ConfigurationSchema), ConfigurationSchema,
            AepSchemaDigest.Compute(EnvelopeSchema), EnvelopeSchema, false, [],
            new(true, true, true, true), new(100, 65536, 65536),
            new(AspProtocol.Profile, "content", "asp", FileSystemId, Version))],
        "Bounded isolated filesystem content through Apache OpenDAL; no historical versions or Snapshot retention."),
        RemoteDescriptor("opendal.s3", "OpenDAL S3 (descriptor only)", "bucket", "access-key", "secret-key"),
        RemoteDescriptor("opendal.azblob", "OpenDAL Azure Blob (descriptor only)", "container", "account", "account-key")
    ];

    private static AepStorageTypeContribution RemoteDescriptor(string id, string displayName, string destination,
        string identity, string credential)
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object", additionalProperties = false, required = new[] { destination },
            properties = new Dictionary<string, object> { [destination] = new { type = "string", minLength = 1, maxLength = 256 } }
        });
        const string version = "0.1.0-preview.1";
        return new(id, displayName, version,
            [new(version, AepSchemaDigest.Compute(schema), schema, AepSchemaDigest.Compute(EnvelopeSchema), EnvelopeSchema,
                true, [new("endpoint", true, AepValueType.Text, AepValueProtection.Standard),
                    new(identity, true, AepValueType.Text, AepValueProtection.Standard),
                    new(credential, true, AepValueType.Text, AepValueProtection.Secured)],
                new(false, false, false, false), new(100, 65536, 65536),
                new(AspProtocol.Profile, "content", "asp", id, version))],
            "Metadata only: no executable remote backend. Configuration leases and every content operation are refused.");
    }
}

public sealed record OpenDalEnvelope(string Prefix = "", bool List = true, bool Stat = true, bool Read = true,
    bool Write = false, int MaximumReadBytes = 65536, int MaximumWriteBytes = 65536, int MaximumPageSize = 100,
    string? KeyFrom = null, string? KeyBefore = null)
{
    public AspAccessPolicy ToPolicy() => new(List, Stat, Read, Write, Prefix, MaximumReadBytes, MaximumWriteBytes,
        MaximumPageSize, KeyFrom, KeyBefore);
}
