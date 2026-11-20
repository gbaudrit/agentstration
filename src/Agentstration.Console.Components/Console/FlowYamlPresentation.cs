using System.Text.Json;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Agentstration.Web.Console;

public static class FlowYamlPresentation
{
    private static readonly JsonSerializerOptions GraphJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ISerializer GraphSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .DisableAliases()
        .Build();

    public static string ToYaml(FlowResponse resource) => ResourceManifestSerializer.ToYaml(new
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = FlowResourceKinds.Flow,
        Metadata = new { resource.Namespace, Name = resource.Id },
        resource.ScopeRef,
        Definition = new
        {
            DisplayName = resource.DisplayName ?? resource.Name,
            resource.Description,
            resource.Version,
            resource.Enabled,
            Spec = resource.Definition,
            resource.Metadata,
            resource.Graph
        }
    });

    // The designer consumes graph source, while resource details export the full manifest.
    public static string ToYaml(FlowGraphDefinition definition)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(definition, GraphJsonOptions));
        return GraphSerializer.Serialize(Normalize(document.RootElement));
    }

    private static object? Normalize(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(property => property.Name, property => Normalize(property.Value), StringComparer.Ordinal),
        JsonValueKind.Array => value.EnumerateArray().Select(Normalize).ToArray(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };
}
