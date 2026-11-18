using Microsoft.AspNetCore.Components;

namespace Agentstration.Web.Components;

public sealed record CreationWizardStep(string Id, string Title, RenderFragment Content)
{
    public bool Visible { get; init; } = true;
}

public sealed class CreationWizardStepChange(string from, string to)
{
    public string From { get; } = from;
    public string To { get; } = to;
    public bool Cancel { get; set; }
}
