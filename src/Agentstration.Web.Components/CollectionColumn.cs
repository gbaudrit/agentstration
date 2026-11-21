using Microsoft.AspNetCore.Components;

namespace Agentstration.Web.Components;

/// <summary>One presentation definition shared by table headings, card values and applicable filters.</summary>
public sealed record CollectionColumn<TItem>(string Key, string Label, RenderFragment<TItem> Content)
{
    public RenderFragment? Heading { get; init; }
    public IReadOnlyList<CollectionFilter> Filters { get; init; } = [];
}
