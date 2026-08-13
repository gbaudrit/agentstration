using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;

namespace Agentstration.Web.Console;

public sealed class ModelProviderEditorModel
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    [Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    public string Name { get; set; } = string.Empty;
    [Required] public string DisplayName { get; set; } = string.Empty;
    [Required] public string ProviderType { get; set; } = "ollama";
    [Required, Url] public string Endpoint { get; set; } = "http://localhost:5260";
    public ModelProviderManagementMode ManagementMode { get; set; } = ModelProviderManagementMode.External;
    public string? ProviderOptionsJson { get; set; }

    public CreateModelProviderRequest ToCreateRequest() => new(Name.Trim(), ToProperties());
    public PutModelProviderRequest ToPutRequest() => new(ToProperties());

    public ModelProviderProperties ToProperties()
    {
        if (!Uri.TryCreate(Endpoint.Trim(), UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
            throw new ArgumentException("Endpoint must be an absolute HTTP(S) URL.");
        return new ModelProviderProperties
        {
            DisplayName = DisplayName.Trim(),
            ProviderType = ProviderType.Trim(),
            Endpoint = endpoint,
            ManagementMode = ManagementMode,
            ProviderOptions = ParseOptions(ProviderOptionsJson)
        };
    }

    public static ModelProviderEditorModel FromResource(ModelProviderResource resource) => new()
    {
        Name = resource.Name,
        DisplayName = resource.Properties.DisplayName,
        ProviderType = resource.Properties.ProviderType,
        Endpoint = resource.Properties.Endpoint.AbsoluteUri.TrimEnd('/'),
        ManagementMode = resource.Properties.ManagementMode,
        ProviderOptionsJson = resource.Properties.ProviderOptions.Count == 0
            ? null
            : JsonSerializer.Serialize(resource.Properties.ProviderOptions, IndentedJson)
    };

    private static IReadOnlyDictionary<string, JsonElement> ParseOptions(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new Dictionary<string, JsonElement>();
        try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(value) ?? new Dictionary<string, JsonElement>(); }
        catch (JsonException exception) { throw new ArgumentException("Provider options must be a JSON object.", exception); }
    }
}
