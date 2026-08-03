using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;

namespace Agentstration.Web.Console;

public sealed class RuntimeProfileEditorModel
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
    [Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    public string Name { get; set; } = string.Empty;
    [Required] public string ResourceGroup { get; set; } = "default";
    [Required] public string Location { get; set; } = "local";
    [Required] public string DisplayName { get; set; } = string.Empty;
    [Required] public string RuntimeType { get; set; } = "microsoft-agent-framework";
    public RuntimeSessionMode SessionMode { get; set; } = RuntimeSessionMode.Transient;
    public RuntimeToolInvocationMode ToolInvocation { get; set; } = RuntimeToolInvocationMode.Automatic;
    public StreamingMode Streaming { get; set; } = StreamingMode.Automatic;
    public string? RuntimeOptionsJson { get; set; }

    public CreateRuntimeProfileRequest ToCreateRequest() => new(Name.Trim(), ResourceGroup.Trim(), Location.Trim(), ToProperties());
    public PutRuntimeProfileRequest ToPutRequest() => new(ToProperties());
    public RuntimeProfileProperties ToProperties() => new()
    {
        DisplayName = DisplayName.Trim(),
        RuntimeType = RuntimeType.Trim(),
        Execution = new RuntimeExecutionDefaults { SessionMode = SessionMode, ToolInvocation = ToolInvocation, Streaming = Streaming },
        RuntimeOptions = ParseOptions(RuntimeOptionsJson)
    };

    public static RuntimeProfileEditorModel FromResource(RuntimeProfileResource resource) => new()
    {
        Name = resource.Name,
        ResourceGroup = resource.ResourceGroup ?? "default",
        Location = resource.Location ?? "local",
        DisplayName = resource.Properties.DisplayName,
        RuntimeType = resource.Properties.RuntimeType,
        SessionMode = resource.Properties.Execution.SessionMode,
        ToolInvocation = resource.Properties.Execution.ToolInvocation,
        Streaming = resource.Properties.Execution.Streaming,
        RuntimeOptionsJson = resource.Properties.RuntimeOptions.Count == 0
            ? null
            : JsonSerializer.Serialize(resource.Properties.RuntimeOptions, IndentedJson)
    };

    private static IReadOnlyDictionary<string, JsonElement> ParseOptions(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new Dictionary<string, JsonElement>();
        try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(value) ?? new Dictionary<string, JsonElement>(); }
        catch (JsonException exception) { throw new ArgumentException("Runtime options must be a JSON object.", exception); }
    }
}
