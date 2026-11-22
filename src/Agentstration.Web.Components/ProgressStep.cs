using Microsoft.AspNetCore.Components;

namespace Agentstration.Web.Components;

public enum ProgressStepStatus
{
    Pending,
    Running,
    Waiting,
    Succeeded,
    Failed,
    Skipped,
    Cancelled,
    Interrupted
}

/// <summary>A presentation step. Its content and execution state belong to the caller.</summary>
public sealed record ProgressStep(string Id, string Title, ProgressStepStatus Status, RenderFragment Content)
{
    public string? Summary { get; init; }
    public string? IconName { get; init; }
    public bool Selectable { get; init; } = true;
    public string? ParentId { get; init; }
}
