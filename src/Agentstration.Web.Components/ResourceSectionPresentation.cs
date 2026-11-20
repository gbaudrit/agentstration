using Microsoft.AspNetCore.Components;

namespace Agentstration.Web.Components;

/// <summary>Presentation requirements and authorized commands for one resource section.</summary>
public sealed record ResourceSectionPresentation
{
    public bool HasRequiredFields { get; init; }
    public bool ReadOnly { get; init; }
    public ResourceReadOnlyReason? ReadOnlyReason { get; init; }
    public string? ReadOnlyMessage { get; init; }
    public IReadOnlyList<string> Fields { get; init; } = [];
    public RenderFragment? Actions { get; init; }
    public RenderFragment? ReadOnlyActions { get; init; }
}
