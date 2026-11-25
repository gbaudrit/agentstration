using System.Text.Json;
using Agentstration.Coding.Contracts;

namespace Agentstration.Coding;

/// <summary>Bounded produced code, separated from materialized harness files and executable host authority.</summary>
public static class CodingCodeChanges
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static bool Valid(IReadOnlyList<CodingFileChange>? changes)
    {
        if (changes is not { Count: > 0 and <= CodingExecutionProtocol.MaximumCodeFiles }
            || changes.Any(change => change is null || !Path(change.Path) || Reserved(change.Path))
            || changes.Select(change => change.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != changes.Count
            || JsonSerializer.SerializeToUtf8Bytes(changes, Json).Length > CodingExecutionProtocol.MaximumCodeChangesBytes) return false;
        try
        {
            return changes.All(change => change.ContentBase64 is null ? !change.Executable
                : Convert.FromBase64String(change.ContentBase64).Length <= 24 * 1024);
        }
        catch (FormatException) { return false; }
    }
    public static bool Path(string? value) => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl)
        && !value.Contains('\\', StringComparison.Ordinal) && !value.Contains(':', StringComparison.Ordinal)
        && !value.StartsWith('/') && value.Split('/').All(segment => segment.Length > 0 && segment is not ("." or "..")
            && !segment.Equals(".git", StringComparison.OrdinalIgnoreCase));
    public static bool Reserved(string value) => value.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase)
        || value.Equals("AGENTS.override.md", StringComparison.OrdinalIgnoreCase)
        || value.Equals(CodexHarnessMaterializer.ProvenancePath, StringComparison.OrdinalIgnoreCase)
        || value.Equals(".codex", StringComparison.OrdinalIgnoreCase) || value.StartsWith(".codex/", StringComparison.OrdinalIgnoreCase);
}
