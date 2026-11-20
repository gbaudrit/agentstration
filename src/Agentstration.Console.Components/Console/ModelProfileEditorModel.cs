using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Agentstration.Models;
using Agentstration.Models.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public sealed class ModelProfileEditorModel
{
    private static readonly JsonSerializerOptions IndentedJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    [Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    public string Name { get; set; } = string.Empty;

    [Required] public string Namespace { get; set; } = ResourceNamespace.DefaultValue;

    [Required] public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    [Required] public ResourceScopeRef? ScopeRef { get; set; }
    public string Tags { get; set; } = string.Empty;
    private ResourceReference? originalProvider;
    private string? originalProviderNamespace;
    [Required] public string ProviderNamespace { get; set; } = ResourceNamespace.DefaultValue;
    [Required] public string ProviderName { get; set; } = string.Empty;
    [Required] public string ModelName { get; set; } = string.Empty;
    [Range(0d, 2d)] public double? Temperature { get; set; }
    [Range(0d, 1d)] public double? TopP { get; set; }
    [Range(1, int.MaxValue)] public int? TopK { get; set; }
    [Range(1, int.MaxValue)] public int? MaxOutputTokens { get; set; }
    public int? Seed { get; set; }
    public string? StopSequences { get; set; }
    public ReasoningMode ReasoningMode { get; set; } = ReasoningMode.Automatic;
    public ReasoningEffort? ReasoningEffort { get; set; }
    public ModelOutputFormat OutputFormat { get; set; } = ModelOutputFormat.Text;
    public string? JsonSchema { get; set; }
    public bool StrictOutput { get; set; }
    public string? ProviderOptionsJson { get; set; }

    public CreateModelProfileRequest ToCreateRequest() => new(Name.Trim(), ToProperties(), ResourceNamespace.Parse(Namespace).Value, ScopeRef);
    public PutModelProfileRequest ToPutRequest() => new(ToProperties());

    public ModelProfileProperties ToProperties()
    {
        if (string.IsNullOrWhiteSpace(ProviderName)) throw new ArgumentException("A model provider name is required.");
        if (string.IsNullOrWhiteSpace(ModelName)) throw new ArgumentException("A model must be selected.");
        return new ModelProfileProperties
        {
            DisplayName = DisplayName.Trim(),
            Description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
            Provider = originalProvider is not null && originalProvider.Name == ProviderName && originalProviderNamespace == ProviderNamespace
                ? originalProvider : new ResourceReference(ProviderName.Trim(), ResourceScopeRef.Instance, ResourceNamespace.Parse(ProviderNamespace)),
            Model = new ModelSelection { Name = ModelName.Trim() },
            Generation = new ModelGenerationOptions
            {
                Temperature = Temperature,
                TopP = TopP,
                TopK = TopK,
                MaxOutputTokens = MaxOutputTokens,
                Seed = Seed,
                StopSequences = ParseLines(StopSequences)
            },
            Reasoning = new ModelReasoningOptions { Mode = ReasoningMode, Effort = ReasoningEffort },
            Output = new ModelOutputOptions
            {
                Format = OutputFormat,
                JsonSchema = ParseJson(JsonSchema, OutputFormat == ModelOutputFormat.JsonSchema),
                Strict = StrictOutput
            },
            ProviderOptions = ParseProviderOptions(ProviderOptionsJson)
        };
    }

    public static ModelProfileEditorModel FromResource(ModelProfileResource resource) => new()
    {
        Name = resource.Name,
        ScopeRef = resource.ScopeRef,
        Tags = string.Join(Environment.NewLine, resource.Metadata.Tags.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}")),
        originalProvider = resource.Definition.Provider,
        originalProviderNamespace = (resource.Definition.Provider.Namespace ?? resource.Namespace).Value,
        Namespace = resource.Namespace.Value,
        DisplayName = resource.Definition.DisplayName,
        Description = resource.Definition.Description,
        ProviderNamespace = (resource.Definition.Provider.Namespace ?? resource.Namespace).Value,
        ProviderName = resource.Definition.Provider.Name,
        ModelName = resource.Definition.Model.Name,
        Temperature = resource.Definition.Generation.Temperature,
        TopP = resource.Definition.Generation.TopP,
        TopK = resource.Definition.Generation.TopK,
        MaxOutputTokens = resource.Definition.Generation.MaxOutputTokens,
        Seed = resource.Definition.Generation.Seed,
        StopSequences = resource.Definition.Generation.StopSequences is { Count: > 0 } stops ? string.Join(Environment.NewLine, stops) : null,
        ReasoningMode = resource.Definition.Reasoning.Mode,
        ReasoningEffort = resource.Definition.Reasoning.Effort,
        OutputFormat = resource.Definition.Output.Format,
        JsonSchema = resource.Definition.Output.JsonSchema is { } schema ? JsonSerializer.Serialize(schema, IndentedJson) : null,
        StrictOutput = resource.Definition.Output.Strict,
        ProviderOptionsJson = resource.Definition.ProviderOptions.Count > 0
            ? JsonSerializer.Serialize(resource.Definition.ProviderOptions, IndentedJson)
            : null
    };

    public IReadOnlySet<string> InvalidFields()
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(this, new ValidationContext(this), results, true);
        return results.SelectMany(result => result.MemberNames).Concat(InputIssues().Select(issue => issue.Field)).ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlyList<ModelProfileInputIssue> InputIssues()
    {
        var issues = new List<ModelProfileInputIssue>();
        try { ParseJson(JsonSchema, OutputFormat == ModelOutputFormat.JsonSchema); }
        catch (ArgumentException) { issues.Add(new(nameof(JsonSchema), ModelProfileInputIssueCode.JsonSchemaInvalid)); }
        try { ParseProviderOptions(ProviderOptionsJson); }
        catch (ArgumentException) { issues.Add(new(nameof(ProviderOptionsJson), ModelProfileInputIssueCode.ProviderOptionsInvalid)); }
        return issues;
    }

    private static IReadOnlyList<string>? ParseLines(string? value)
    {
        var values = value?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return values is { Length: > 0 } ? values : null;
    }

    private static JsonElement? ParseJson(string? value, bool required)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required) throw new ArgumentException("A JSON schema is required for JSON schema output.");
            return null;
        }
        try { return JsonDocument.Parse(value).RootElement.Clone(); }
        catch (JsonException exception) { throw new ArgumentException("The JSON schema is not valid JSON.", exception); }
    }

    private static IReadOnlyDictionary<string, VersionedExtensionOptions> ParseProviderOptions(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new Dictionary<string, VersionedExtensionOptions>();
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, VersionedExtensionOptions>>(value, IndentedJson)
                ?? new Dictionary<string, VersionedExtensionOptions>();
        }
        catch (JsonException exception) { throw new ArgumentException("Provider options must be a JSON object.", exception); }
    }
}

public enum ModelProfileInputIssueCode { JsonSchemaInvalid, ProviderOptionsInvalid }
public sealed record ModelProfileInputIssue(string Field, ModelProfileInputIssueCode Code);

public static class ModelManagementUi
{
    public static Agentstration.Web.Components.Models.UiStatus Status(string? status) => status?.ToLowerInvariant() switch
    {
        "available" or "ready" or "succeeded" => Agentstration.Web.Components.Models.UiStatus.Success,
        "starting" or "providerunavailable" or "modelunavailable" or "unavailable" => Agentstration.Web.Components.Models.UiStatus.Warning,
        "invalidconfiguration" or "incompatible" or "failed" => Agentstration.Web.Components.Models.UiStatus.Danger,
        _ => Agentstration.Web.Components.Models.UiStatus.Neutral
    };

    public static string Label(string? status) => status switch
    {
        "providerUnavailable" => "Provider unavailable",
        "modelUnavailable" => "Model unavailable",
        "invalidConfiguration" => "Invalid configuration",
        null or "" => "Unknown",
        _ => char.ToUpperInvariant(status[0]) + status[1..]
    };

    public static bool IsInvalid(ModelProfileSummaryResponse profile) =>
        string.Equals(profile.Properties.Status, "invalidConfiguration", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<ModelProfileSummaryResponse> FilterProfiles(IEnumerable<ModelProfileSummaryResponse> profiles, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return profiles.ToArray();
        return profiles.Where(item => item.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.Properties.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.Properties.Provider.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
            || item.Properties.Model.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
    }
}
