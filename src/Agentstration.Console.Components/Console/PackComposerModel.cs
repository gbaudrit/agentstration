using System.ComponentModel.DataAnnotations;
using Agentstration.Packs.Contracts;

namespace Agentstration.Web.Console;

public sealed class PackComposerModel
{
    [Required] public string Publisher { get; set; } = "local";
    [Required] public string Name { get; set; } = string.Empty;
    [Required] public string Version { get; set; } = "0.1.0";
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public PackAudience Audience { get; set; } = PackAudience.Universal;
    public PackPurpose Purpose { get; set; } = PackPurpose.Standard;
    public string Categories { get; set; } = string.Empty;
    public string Tags { get; set; } = string.Empty;
    public bool CoordinatesPresent => !string.IsNullOrWhiteSpace(Publisher) && !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(Version);
    public CreatePackProjectFromWorkspaceCommand Command(IReadOnlyList<PackCompositionResourceKey> resources) => new()
    {
        Publisher = Publisher.Trim(), Name = Name.Trim(), Version = Version.Trim(), DisplayName = Empty(DisplayName), Description = Empty(Description),
        Audience = Audience, Purpose = Purpose, Categories = Split(Categories), Tags = Split(Tags), Resources = resources
    };
    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static IReadOnlyList<string> Split(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
