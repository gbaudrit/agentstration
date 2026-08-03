using System.Text.Json;

namespace Agentstration.ModelProviders.Ollama;

public enum OllamaThinkOption { Disabled, Enabled, Low, Medium, High }
public enum OllamaEndpointMode { Automatic, Chat, Generate }

public sealed record OllamaModelOptions
{
    public OllamaThinkOption? Think { get; init; }
    public TimeSpan? KeepAlive { get; init; }
    public int? ContextSize { get; init; }
    public int? NumGpu { get; init; }
    public int? NumThread { get; init; }
    public int? NumBatch { get; init; }
    public int? Mirostat { get; init; }
    public OllamaEndpointMode EndpointMode { get; init; } = OllamaEndpointMode.Automatic;
    public IReadOnlyDictionary<string, JsonElement> AdditionalOptions { get; init; } = new Dictionary<string, JsonElement>();
}

public static class OllamaModelOptionsParser
{
    public static OllamaModelOptions Parse(IReadOnlyDictionary<string, JsonElement> providerOptions)
    {
        ArgumentNullException.ThrowIfNull(providerOptions);
        if (!providerOptions.TryGetValue(OllamaModelProvider.ProviderTypeName, out var value)) return new();
        if (value.ValueKind != JsonValueKind.Object)
            throw new ModelProviderConfigurationException("providerOptions.ollama must be a JSON object.");

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "think", "keepAlive", "contextSize", "numGpu", "numThread", "numBatch", "mirostat", "endpointMode", "additionalOptions"
        };
        var result = new OllamaModelOptions
        {
            Think = ParseEnum<OllamaThinkOption>(value, "think"),
            KeepAlive = ParseDuration(value, "keepAlive"),
            ContextSize = ReadInt(value, "contextSize"),
            NumGpu = ReadInt(value, "numGpu"),
            NumThread = ReadInt(value, "numThread"),
            NumBatch = ReadInt(value, "numBatch"),
            Mirostat = ReadInt(value, "mirostat"),
            EndpointMode = ParseEnum<OllamaEndpointMode>(value, "endpointMode") ?? OllamaEndpointMode.Automatic,
            AdditionalOptions = ReadAdditional(value)
        };
        foreach (var property in value.EnumerateObject())
        {
            if (!known.Contains(property.Name))
                throw new ModelProviderConfigurationException($"Unknown typed Ollama option '{property.Name}'. Put future native options under additionalOptions.");
        }
        Validate(result);
        return result;
    }

    public static void Validate(OllamaModelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.KeepAlive is { } keepAlive && keepAlive <= TimeSpan.Zero)
            throw new ModelProviderConfigurationException("Ollama keepAlive must be positive.");
        if (options.ContextSize is <= 0 || options.NumGpu is < 0 || options.NumThread is <= 0 || options.NumBatch is <= 0)
            throw new ModelProviderConfigurationException("Ollama engine sizes must be positive (numGpu may be zero).");
        if (options.Mirostat is < 0 or > 2)
            throw new ModelProviderConfigurationException("Ollama mirostat must be 0, 1, or 2.");
        if (options.EndpointMode == OllamaEndpointMode.Generate)
            throw new ModelProviderConfigurationException("Ollama endpointMode 'generate' is incompatible with the Microsoft Agent Framework chat adapter.");
    }

    private static T? ParseEnum<T>(JsonElement value, string name) where T : struct, Enum
    {
        if (!value.TryGetProperty(name, out var property)) return null;
        if (property.ValueKind == JsonValueKind.True && typeof(T) == typeof(OllamaThinkOption)) return (T)(object)OllamaThinkOption.Enabled;
        if (property.ValueKind == JsonValueKind.False && typeof(T) == typeof(OllamaThinkOption)) return (T)(object)OllamaThinkOption.Disabled;
        if (property.ValueKind != JsonValueKind.String || !Enum.TryParse<T>(property.GetString(), true, out var parsed))
            throw new ModelProviderConfigurationException($"Ollama option '{name}' has an invalid value.");
        return parsed;
    }

    private static int? ReadInt(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)) return null;
        if (!property.TryGetInt32(out var result)) throw new ModelProviderConfigurationException($"Ollama option '{name}' must be an integer.");
        return result;
    }

    private static TimeSpan? ParseDuration(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)) return null;
        if (property.ValueKind != JsonValueKind.String) throw new ModelProviderConfigurationException($"Ollama option '{name}' must be a duration string.");
        var text = property.GetString()!;
        if (TimeSpan.TryParse(text, out var duration)) return duration;
        if (text.Length > 1 && double.TryParse(text[..^1], System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            var parsed = char.ToLowerInvariant(text[^1]) switch
            {
                's' => TimeSpan.FromSeconds(number),
                'm' => TimeSpan.FromMinutes(number),
                'h' => TimeSpan.FromHours(number),
                _ => (TimeSpan?)null
            };
            if (parsed is not null) return parsed;
        }
        throw new ModelProviderConfigurationException($"Ollama option '{name}' must use a duration such as '10m'.");
    }

    private static IReadOnlyDictionary<string, JsonElement> ReadAdditional(JsonElement value)
    {
        if (!value.TryGetProperty("additionalOptions", out var property)) return new Dictionary<string, JsonElement>();
        if (property.ValueKind != JsonValueKind.Object) throw new ModelProviderConfigurationException("Ollama additionalOptions must be a JSON object.");
        return property.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal);
    }
}
