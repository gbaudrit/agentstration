using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Asp.Abstractions;
using Agentstration.Asp.OData;

namespace Agentstration.Extensions.PostgreSql;

public static class PostgreSqlStorageTypes
{
    public const string ExtensionId = "agentstration.extensions.postgresql";
    public const string TypeId = "postgresql.jsonb";
    public const string Version = "1.0.0";
    public static JsonElement ConfigurationSchema { get; } = JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false });
    public static JsonElement EnvelopeSchema { get; } = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            get = new { type = "boolean" },
            put = new { type = "boolean" },
            delete = new { type = "boolean" },
            query = new { type = "boolean" },
            publish = new { type = "boolean" },
            maximumPageSize = new { type = "integer", minimum = 1, maximum = 100 },
            maximumDocumentBytes = new { type = "integer", minimum = 1, maximum = 65536 },
            maximumResultBytes = new { type = "integer", minimum = 65536, maximum = 196608 },
            queryProfiles = new { type = "array", minItems = 1, maxItems = 8, items = new { type = "string", maxLength = 128 } }
        }
    });
    public static IReadOnlyList<AepStorageRequirement> Requirements { get; } =
    [new("host", true, AepValueType.Text, AepValueProtection.Standard),
     new("port", true, AepValueType.WholeNumber, AepValueProtection.Standard),
     new("database", true, AepValueType.Text, AepValueProtection.Standard),
     new("username", true, AepValueType.Text, AepValueProtection.Standard),
     new("password", true, AepValueType.Text, AepValueProtection.Secured)];
    public static IReadOnlyList<AepStorageTypeContribution> Contributions { get; } =
    [new(TypeId, "PostgreSQL JSONB documents", Version,
        [new(Version, AepSchemaDigest.Compute(ConfigurationSchema), ConfigurationSchema,
            AepSchemaDigest.Compute(EnvelopeSchema), EnvelopeSchema, true, Requirements,
            new(false, false, false, false), new(100, 65536, 65536),
            new(AspDocuments.Profile, "documents", "aspdoc", "postgresql-jsonb", Version))
        { Documents = new(true, true, true, true, true, [new(ODataDocumentProfile.Id, ODataDocumentProfile.Version, ODataDocumentProfile.Encoding)]) }])];
}

/// <summary>Independent service contribution; Storage binds compatible exports, never this contributor's identity.</summary>
public static class PostgreSqlConnectedServiceType
{
    public static AepConnectedServiceTypeContribution Contribution { get; } = new("postgresql", "PostgreSQL", "1.0.0",
        [AepConnectedServiceTypeVersionDescriptor.Create("1.0.0", PostgreSqlStorageTypes.ConfigurationSchema,
            PostgreSqlStorageTypes.Requirements.Select(r => new AepConnectedServiceInputDescriptor(r.Id, r.Required, r.Type, r.Protection)).ToArray(),
            PostgreSqlStorageTypes.Requirements.Select(r => new AepConnectedServiceExportDescriptor(r.Id, r.Type, r.Protection) { SourceInputId = r.Id }).ToArray())]);
}

public sealed record PostgreSqlDocumentEnvelope(bool Get = true, bool Put = false, bool Delete = false,
    bool Query = true, bool Publish = false, int MaximumPageSize = 50, int MaximumDocumentBytes = 65536,
    int MaximumResultBytes = 196608, IReadOnlyList<string>? QueryProfiles = null)
{
    public AspDocumentPolicy For(string collection) => new(collection, Get, Put, Delete, Query, Publish,
        MaximumPageSize, MaximumDocumentBytes, MaximumResultBytes, QueryProfiles ?? [ODataDocumentProfile.Id]);
}
