namespace Agentstration.Web.Components;

/// <summary>Successful page facts. Cursor adapters map sequential callbacks to their own cursor history.</summary>
public sealed record CollectionPagination(int Page, int PageSize, bool HasMore)
{
    public int? Total { get; init; }
    private bool? hasPrevious;
    public bool HasPrevious { get => hasPrevious ?? Page > 1; init => hasPrevious = value; }
    public bool SupportsDirectNavigation { get; init; }
    public IReadOnlyList<int> PageSizes { get; init; } = [20, 50, 100];
}
