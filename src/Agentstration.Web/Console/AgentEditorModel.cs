using System.ComponentModel.DataAnnotations;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;

namespace Agentstration.Web.Console;

public sealed class AgentEditorModel
{
    [Required, RegularExpression("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    public string Name { get; set; } = string.Empty;
    [Required] public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    [Required] public string Handler { get; set; } = "prompt-agent";
    [Required] public string Instructions { get; set; } = string.Empty;
    [Required] public string ModelProfileName { get; set; } = "reasoning-default";
    public bool MemoryEnabled { get; set; }
    public string MemoryProfileName { get; set; } = "default-memory";
    public bool ReadOwnMemory { get; set; } = true;
    public string SharedMemoryScopes { get; set; } = string.Empty;
    public string ToolNames { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;
    public string Annotations { get; set; } = string.Empty;

    public AgentResourceRequest ToRequest()
    {
        var tools = Lines(ToolNames).Select(value => new ResourceReference(value)).ToArray();
        if (tools.Select(tool => tool.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != tools.Length)
            throw new ArgumentException("Tool names cannot be duplicated.");
        return new AgentResourceRequest
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.Agent,
            Metadata = new ResourceMetadata { Name = Name.Trim(), Tags = ParseMap(Tags, "tag"), Annotations = ParseMap(Annotations, "annotation") },
            Definition = new AgentProperties
            {
                DisplayName = DisplayName.Trim(),
                Description = NullIfWhiteSpace(Description),
                Handler = Handler.Trim(),
                Instructions = Instructions.Trim(),
                ModelProfile = new ResourceReference(ModelProfileName.Trim()),
                Memory = MemoryEnabled ? new AgentMemoryConfiguration
                {
                    Profile = new ResourceReference(MemoryProfileName.Trim()),
                    ReadOwnMemory = ReadOwnMemory,
                    SharedScopes = Lines(SharedMemoryScopes)
                } : null,
                Tools = tools
            }
        };
    }

    public static AgentEditorModel FromResource(AgentResource resource) => new()
    {
        Name = resource.Metadata.Name,
        DisplayName = resource.Definition.DisplayName,
        Description = resource.Definition.Description,
        Handler = resource.Definition.Handler,
        Instructions = resource.Definition.Instructions,
        ModelProfileName = resource.Definition.ModelProfile.Name,
        MemoryEnabled = resource.Definition.Memory is not null,
        MemoryProfileName = resource.Definition.Memory?.Profile.Name ?? "default-memory",
        ReadOwnMemory = resource.Definition.Memory?.ReadOwnMemory ?? true,
        SharedMemoryScopes = string.Join(Environment.NewLine, resource.Definition.Memory?.SharedScopes ?? []),
        ToolNames = string.Join(Environment.NewLine, resource.Definition.Tools.Select(tool => tool.Name)),
        Tags = Format(resource.Metadata.Tags),
        Annotations = Format(resource.Metadata.Annotations)
    };

    private static IReadOnlyDictionary<string, string> ParseMap(string value, string label)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Lines(value))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0 || separator == line.Length - 1) throw new ArgumentException($"Each {label} must use key=value.");
            if (!result.TryAdd(line[..separator].Trim(), line[(separator + 1)..].Trim())) throw new ArgumentException($"Duplicate {label} key.");
        }
        return result;
    }

    private static string Format(IReadOnlyDictionary<string, string> values) => string.Join(Environment.NewLine, values.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));
    private static string[] Lines(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
