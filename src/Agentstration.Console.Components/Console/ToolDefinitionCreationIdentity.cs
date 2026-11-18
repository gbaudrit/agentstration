using System.Text.RegularExpressions;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public sealed class ToolDefinitionCreationIdentity
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Namespace { get; set; } = "default";
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = string.Empty;
    public ResourceScopeRef? ScopeRef { get; set; }

    public bool IsComplete => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(DisplayName)
        && !string.IsNullOrWhiteSpace(Namespace)
        && Version.Length <= 29
        && Regex.IsMatch(Version, @"^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
}
