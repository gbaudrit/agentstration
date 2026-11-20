using System.ComponentModel.DataAnnotations;
using Agentstration.Flows.Contracts;

namespace Agentstration.Web.Console;

/// <summary>The metadata of the existing Workspace-owned Flow draft; its graph stays family-owned.</summary>
public sealed class FlowDefinitionEditorModel
{
    public string Name { get; set; } = string.Empty;
    public string Namespace { get; set; } = string.Empty;
    [Required] public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Tags { get; set; } = string.Empty;

    // The existing metadata command checks the submitted, trimmed name.
    public bool HasValidDisplayName => !string.IsNullOrWhiteSpace(DisplayName) && DisplayName.Trim().Length <= 200;

    public static FlowDefinitionEditorModel From(FlowResponse saved, FlowDraftResponse? draft = null) => new()
    {
        Name = saved.Id,
        Namespace = saved.Namespace.Value,
        DisplayName = draft?.Value.DisplayName ?? saved.DisplayName ?? saved.Name,
        Description = draft is null ? saved.Description : draft.Value.Description,
        Tags = Format(draft?.Value.Tags ?? saved.Metadata)
    };

    public IReadOnlyDictionary<string, string> TagValues()
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Tags.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0 || string.IsNullOrWhiteSpace(line[..separator]) || string.IsNullOrWhiteSpace(line[(separator + 1)..])
                || !tags.TryAdd(line[..separator].Trim(), line[(separator + 1)..].Trim()))
                throw new ArgumentException("Invalid or duplicate Flow tag.", nameof(Tags));
        }
        return tags;
    }

    public static string Format(IReadOnlyDictionary<string, string> tags) => string.Join(Environment.NewLine,
        tags.OrderBy(value => value.Key, StringComparer.Ordinal).Select(value => $"{value.Key}={value.Value}"));
}
