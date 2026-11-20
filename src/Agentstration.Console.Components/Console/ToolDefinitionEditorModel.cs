using System.ComponentModel.DataAnnotations;
using Agentstration.Tools;

namespace Agentstration.Web.Console;

public sealed class ToolDefinitionEditorModel
{
    public string Name { get; set; } = string.Empty;
    public string Namespace { get; set; } = "default";
    [Required] public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Tags { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public bool RequiresApproval { get; set; }
    [Required, Range(1, 900)] public int? TimeoutSeconds { get; set; } = 90;
    [Required, StringLength(29), RegularExpression(@"^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$")]
    public string PublicationVersion { get; set; } = "1.0.0";
    public bool Publish { get; set; }
    public string SelectedFlow { get; set; } = string.Empty;
    public bool UseActiveVersion { get; set; } = true;
    public string FlowVersion { get; set; } = string.Empty;

    public static ToolDefinitionEditorModel From(ToolDefinitionResource resource) => new()
    {
        Name = resource.Name, Namespace = resource.Namespace.Value, DisplayName = resource.Definition.DisplayName,
        Description = resource.Definition.Description,
        Tags = string.Join(Environment.NewLine, resource.Metadata.Tags.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}")),
        Enabled = resource.Definition.Enabled, RequiresApproval = resource.Definition.RequiresApproval,
        TimeoutSeconds = resource.Definition.InvocationTimeoutSeconds, PublicationVersion = resource.Definition.Version,
        UseActiveVersion = resource.Definition is not FlowToolDefinitionProperties flow || flow.Flow.UseActiveVersion,
        FlowVersion = resource.Definition is FlowToolDefinitionProperties defined ? defined.Flow.Version ?? string.Empty : string.Empty
    };
}
