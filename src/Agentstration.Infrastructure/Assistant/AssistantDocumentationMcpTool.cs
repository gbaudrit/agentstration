using System.Text.Json;
using Agentstration.Tools;

namespace Agentstration.Infrastructure.Assistant;

public sealed record AssistantDocumentationMatch(string Path, string Title, string? Section, string Snippet, int Score);
public sealed record AssistantDocumentationSearchResult(string Availability, string Query, IReadOnlyList<AssistantDocumentationMatch> Matches);

public sealed class AssistantDocumentationCatalog(string rootPath)
{
    private const int MaximumFiles = 1000;
    private const int MaximumFileCharacters = 524_288;
    private const int MaximumSnippetCharacters = 1200;
    private readonly string root = Path.GetFullPath(rootPath);

    public async Task<AssistantDocumentationSearchResult> SearchAsync(
        string query,
        int maximumResults,
        CancellationToken cancellationToken)
    {
        query = query.Trim();
        if (query.Length is < 2 or > 200)
            throw new ToolDefinitionInvocationException("assistant_documentation_query_invalid", "Documentation query must contain between 2 and 200 characters.");
        maximumResults = Math.Clamp(maximumResults, 1, 5);
        if (!Directory.Exists(root)) return new("unavailable", query, []);
        var terms = query.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => value.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
        if (terms.Length == 0)
            throw new ToolDefinitionInvocationException("assistant_documentation_query_invalid", "Documentation query must contain at least one searchable term.");

        var matches = new List<AssistantDocumentationMatch>();
        foreach (var path in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
                     .OrderBy(value => value, StringComparer.Ordinal)
                     .Take(MaximumFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (info.Length > MaximumFileCharacters * 4L) continue;
            var content = await File.ReadAllTextAsync(path, cancellationToken);
            if (content.Length > MaximumFileCharacters) continue;
            var score = terms.Sum(term => Occurrences(content, term));
            if (score == 0) continue;
            var index = terms.Select(term => content.IndexOf(term, StringComparison.OrdinalIgnoreCase))
                .Where(value => value >= 0)
                .DefaultIfEmpty(0)
                .Min();
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            matches.Add(new(relative, Title(content, relative), Section(content, index), Snippet(content, index), score));
        }
        return new(
            matches.Count == 0 ? "no-match" : "available",
            query,
            matches.OrderByDescending(value => value.Score).ThenBy(value => value.Path, StringComparer.Ordinal).Take(maximumResults).ToArray());
    }

    private static int Occurrences(string content, string term)
    {
        var count = 0;
        for (var index = 0; (index = content.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0; index += term.Length)
            count++;
        return count;
    }

    private static string Title(string content, string relativePath) =>
        content.Split('\n').Select(value => value.Trim()).FirstOrDefault(value => value.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim()
        ?? Path.GetFileNameWithoutExtension(relativePath);

    private static string? Section(string content, int index)
    {
        var prefix = content[..Math.Clamp(index, 0, content.Length)];
        var section = prefix.Split('\n').Reverse().Select(value => value.Trim())
            .FirstOrDefault(value => value.StartsWith("## ", StringComparison.Ordinal));
        if (section is not null) return section[3..].Trim();
        return content[index..].Split('\n').Select(value => value.Trim())
            .FirstOrDefault(value => value.StartsWith("## ", StringComparison.Ordinal))?[3..].Trim();
    }

    private static string Snippet(string content, int index)
    {
        var start = Math.Max(0, index - MaximumSnippetCharacters / 3);
        var length = Math.Min(MaximumSnippetCharacters, content.Length - start);
        var snippet = content.Substring(start, length).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return start > 0 ? $"…{snippet}" : snippet;
    }
}

public sealed class AssistantDocumentationMcpTool(AssistantDocumentationCatalog catalog) : IInternalMcpToolHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public InternalMcpToolDefinition Definition { get; } = new(
        AgentstrationInternalTools.AssistantDocumentationSearch,
        "Search Agentstration documentation",
        "Searches the bounded documentation bundled with this Agentstration installation. Results are evidence excerpts with local documentation paths. Use only returned evidence; an unavailable or no-match result must not be replaced with invented product guidance.",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                query = new { type = "string", minLength = 2, maxLength = 200, description = "Concise Agentstration product or configuration question." },
                maximumResults = new { type = "integer", minimum = 1, maximum = 5, @default = 3, description = "Maximum number of bounded documentation excerpts." }
            },
            required = new[] { "query" },
            additionalProperties = false
        }),
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                availability = new { type = "string", @enum = new[] { "available", "no-match", "unavailable" } },
                query = new { type = "string" },
                matches = new { type = "array", items = new { type = "object" } }
            },
            required = new[] { "availability", "query", "matches" },
            additionalProperties = false
        }));

    public async Task<JsonElement?> ExecuteAsync(InternalMcpToolInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Arguments.ValueKind != JsonValueKind.Object)
            throw new ToolDefinitionInvocationException("assistant_documentation_arguments_invalid", "Documentation Tool arguments must be a JSON object.");
        var allowed = new HashSet<string>(["query", "maximumResults"], StringComparer.Ordinal);
        var unknown = invocation.Arguments.EnumerateObject().Select(value => value.Name).FirstOrDefault(value => !allowed.Contains(value));
        if (unknown is not null)
            throw new ToolDefinitionInvocationException("assistant_documentation_argument_unknown", $"Argument '{unknown}' is not declared by the Tool schema.");
        if (!invocation.Arguments.TryGetProperty("query", out var queryValue) || queryValue.ValueKind != JsonValueKind.String)
            throw new ToolDefinitionInvocationException("assistant_documentation_argument_required", "Argument 'query' is required.");
        var maximumResults = invocation.Arguments.TryGetProperty("maximumResults", out var maximumValue)
            && maximumValue.ValueKind == JsonValueKind.Number
            && maximumValue.TryGetInt32(out var parsed)
                ? parsed
                : 3;
        if (maximumResults is < 1 or > 5)
            throw new ToolDefinitionInvocationException("assistant_documentation_argument_invalid", "Argument 'maximumResults' must be between 1 and 5.");
        return JsonSerializer.SerializeToElement(await catalog.SearchAsync(queryValue.GetString()!, maximumResults, cancellationToken), JsonOptions);
    }
}
