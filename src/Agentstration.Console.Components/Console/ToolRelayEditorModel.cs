using System.ComponentModel.DataAnnotations;
using Agentstration.Tools;

namespace Agentstration.Web.Console;

public sealed class ToolRelayEditorModel
{
    public string Name { get; set; } = string.Empty;
    public string Namespace { get; set; } = "default";
    [Required] public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Tags { get; set; } = string.Empty;
    [Required, StringLength(29), RegularExpression(@"^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$")]
    public string Version { get; set; } = "1.0.0";
    public bool Enabled { get; set; } = true;
    public bool RequiresApproval { get; set; }
    [Range(1, 900)] public int Timeout { get; set; } = 90;
    public string SourceKey { get; set; } = string.Empty;
    public string? InputEnvelope { get; set; }
    public IReadOnlyList<ToolArgumentBinding> Bindings { get; set; } = [];
}
