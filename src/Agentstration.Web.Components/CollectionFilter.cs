namespace Agentstration.Web.Components;

public enum CollectionFilterKind { Text, Choice, Number, DateRange }
public enum CollectionFilterApplyMode { Immediate, Explicit }

public sealed record CollectionFilterOption(string Value, string Label);

/// <summary>Localized control metadata only. The adapter owns authorized options and query evaluation.</summary>
public sealed record CollectionFilter(string Key, string Label, CollectionFilterKind Kind = CollectionFilterKind.Text)
{
    public IReadOnlyList<CollectionFilterOption> Options { get; init; } = [];
    public string? Placeholder { get; init; }
    public bool Advanced { get; init; }
}

public sealed record CollectionFilterValue(string Value = "", string End = "")
{
    public bool IsActive => !string.IsNullOrWhiteSpace(Value) || !string.IsNullOrWhiteSpace(End);
}

/// <summary>Requested presentation criteria; contains no transport, query execution or resource data.</summary>
public sealed record CollectionQuery
{
    public string Search { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, CollectionFilterValue> Filters { get; init; } =
        new Dictionary<string, CollectionFilterValue>(StringComparer.Ordinal);
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public bool HasFilters => !string.IsNullOrWhiteSpace(Search) || Filters.Values.Any(value => value.IsActive);

    public CollectionFilterValue Filter(string key) => Filters.GetValueOrDefault(key) ?? new();

    public CollectionQuery WithFilter(string key, CollectionFilterValue value)
    {
        var values = new Dictionary<string, CollectionFilterValue>(Filters, StringComparer.Ordinal);
        if (value.IsActive) values[key] = value;
        else values.Remove(key);
        return this with { Filters = values, Page = 1 };
    }

    public bool HasSameCriteria(CollectionQuery other) =>
        string.Equals(Search, other.Search, StringComparison.Ordinal)
        && Filters.Count == other.Filters.Count
        && Filters.All(pair => other.Filters.TryGetValue(pair.Key, out var value) && pair.Value == value);
}
