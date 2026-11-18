namespace Agentstration.Web.Components;

public sealed record ResourceNavigationItem(string Path, string Label, string Icon)
{
    public bool Visible { get; init; } = true;
    public bool Enabled { get; init; } = true;
    public bool Navigable { get; init; } = true;
}
