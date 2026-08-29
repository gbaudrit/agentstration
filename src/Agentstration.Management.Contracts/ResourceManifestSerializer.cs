using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Agentstration.Management.Contracts;

public static class ResourceManifestSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    static ResourceManifestSerializer() => JsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithAttemptingUnquotedStringTypeDeserialization()
        .Build();

    public static string ToJson<T>(T resource) => JsonSerializer.Serialize(resource, JsonOptions);

    public static T FromJson<T>(string manifest) =>
        JsonSerializer.Deserialize<T>(manifest, JsonOptions)
        ?? throw new JsonException("The resource manifest is empty.");

    public static string ToYaml<T>(T resource)
    {
        var json = JsonSerializer.SerializeToElement(resource, JsonOptions);
        return YamlSerializer.Serialize(FromJsonElement(json));
    }

    public static T FromYaml<T>(string manifest)
    {
        var yaml = YamlDeserializer.Deserialize<object?>(manifest);
        var json = JsonSerializer.Serialize(FromYamlObject(yaml), JsonOptions);
        return FromJson<T>(json);
    }

    public static IReadOnlyList<T> FromYamlDocuments<T>(string manifest)
    {
        var parser = new Parser(new StringReader(manifest));
        parser.Consume<StreamStart>();
        var resources = new List<T>();
        while (parser.Accept<DocumentStart>(out _))
        {
            var yaml = YamlDeserializer.Deserialize<object?>(parser);
            var json = JsonSerializer.Serialize(FromYamlObject(yaml), JsonOptions);
            resources.Add(FromJson<T>(json));
        }
        parser.Consume<StreamEnd>();
        return resources;
    }

    private static object? FromJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(property => property.Name, property => FromJsonElement(property.Value), StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(FromJsonElement).ToArray(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private static object? FromYamlObject(object? value) => value switch
    {
        IDictionary<object, object> map => map.ToDictionary(pair => pair.Key.ToString() ?? string.Empty, pair => FromYamlObject(pair.Value), StringComparer.Ordinal),
        IDictionary<string, object> map => map.ToDictionary(pair => pair.Key, pair => FromYamlObject(pair.Value), StringComparer.Ordinal),
        IEnumerable<object> sequence when value is not string => sequence.Select(FromYamlObject).ToArray(),
        _ => value
    };
}
