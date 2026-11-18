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
        type = "object",
        additionalProperties = false,
        properties = new { }
    });
    public static JsonElement EnvelopeSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            prefix = new { type = "string", maxLength = 256 },
            list = new { type = "boolean" },
            stat = new { type = "boolean" },
            read = new { type = "boolean" },
            write = new { type = "boolean" },
            maximumReadBytes = new { type = "integer", minimum = 1, maximum = 65536 },
            maximumWriteBytes = new { type = "integer", minimum = 1, maximum = 65536 },
            maximumPageSize = new { type = "integer", minimum = 1, maximum = 100 },
            keyFrom = new { type = "string", maxLength = 256 },
            keyBefore = new { type = "string", maxLength = 256 }
        }
    });
    public static IReadOnlyList<AepStorageTypeContribution> Contributions { get; } =
    [
        new(FileSystemId, "OpenDAL filesystem", Version,
        [Descriptor(FileSystemId, ConfigurationSchema, false, [])],
        "Native filesystem objects through Apache OpenDAL. No historical versions or Snapshot retention."),
        new("opendal.s3", "OpenDAL S3", Version,
        [Descriptor("opendal.s3", RemoteSchema("bucket"), true,
            [new("endpoint", true, AepValueType.Text, AepValueProtection.Standard),
             new("region", true, AepValueType.Text, AepValueProtection.Standard),
             new("access-key", true, AepValueType.Text, AepValueProtection.Standard),
             new("secret-key", true, AepValueType.Text, AepValueProtection.Secured)])],
        "S3 objects through the same native OpenDAL adapter; explicit connection bindings are required."),
        new("opendal.azblob", "OpenDAL Azure Blob", Version,
        [Descriptor("opendal.azblob", RemoteSchema("container"), true,
            [new("endpoint", true, AepValueType.Text, AepValueProtection.Standard),
             new("account", true, AepValueType.Text, AepValueProtection.Standard),
             new("account-key", true, AepValueType.Text, AepValueProtection.Secured)])],
        "Azure Blob objects through the same native OpenDAL adapter; explicit connection bindings are required.")
    ];

    private static AepStorageTypeVersion Descriptor(string id, JsonElement schema, bool remote,
        IReadOnlyList<AepStorageRequirement> requirements) =>
        new(Version, AepSchemaDigest.Compute(schema), schema, AepSchemaDigest.Compute(EnvelopeSchema), EnvelopeSchema,
            remote, requirements, new(true, true, true, true), new(100, 65536, 65536),
            new(AspProtocol.Profile, "content", "asp", id, Version));

    private static JsonElement RemoteSchema(string destination) => JsonSerializer.SerializeToElement(new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { destination },
        properties = new Dictionary<string, object>
        {
            [destination] = new { type = "string", minLength = 1, maxLength = 256 },
            ["root"] = new { type = "string", maxLength = 256 }
        }
    });
}

public sealed record OpenDalEnvelope(string Prefix = "", bool List = true, bool Stat = true, bool Read = true,
    bool Write = false, int MaximumReadBytes = 65536, int MaximumWriteBytes = 65536, int MaximumPageSize = 100,
    string? KeyFrom = null, string? KeyBefore = null)
{
    public AspAccessPolicy ToPolicy() => new(List, Stat, Read, Write, Prefix, MaximumReadBytes, MaximumWriteBytes,
        MaximumPageSize, KeyFrom, KeyBefore);
}
