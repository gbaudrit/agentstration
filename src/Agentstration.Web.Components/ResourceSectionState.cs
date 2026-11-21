namespace Agentstration.Web.Components;

/// <summary>Resource-local presentation state, updated from accepted navigation destinations.</summary>
public sealed class ResourceSectionState
{
    private readonly HashSet<string> visited = new(StringComparer.Ordinal);
    private string? identity;

    public string CurrentPath { get; private set; } = "overview";
    public string DefinitionPath { get; private set; } = "definition/identity";
    public bool HasVisitedDefinition => visited.Any(path => path.StartsWith("definition/", StringComparison.Ordinal));
    public bool Is(string path) => string.Equals(CurrentPath, path, StringComparison.Ordinal);
    public bool HasVisited(string path) => visited.Contains(path);

    public void Select(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        CurrentPath = path;
        Visit(path);
        if (path.StartsWith("definition/", StringComparison.Ordinal)) DefinitionPath = path;
    }

    public void Visit(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        visited.Add(path);
    }

    public void Reset(string resourceIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceIdentity);
        if (identity == resourceIdentity) return;
        identity = resourceIdentity;
        CurrentPath = "overview";
        DefinitionPath = "definition/identity";
        visited.Clear();
    }
}
