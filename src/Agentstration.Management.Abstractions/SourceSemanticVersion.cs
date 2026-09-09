using System.Text.RegularExpressions;

namespace Agentstration.Management.Abstractions;

public sealed partial class SourceSemanticVersion
{
    private SourceSemanticVersion(string major, string minor, string patch, string[] prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    private string Major { get; }
    private string Minor { get; }
    private string Patch { get; }
    private string[] Prerelease { get; }

    public static bool TryParse(string? value, out SourceSemanticVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value != value.Trim()) return false;
        var match = SemanticVersionPattern().Match(value);
        if (!match.Success) return false;
        var prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (prerelease.Any(identifier => IsNumeric(identifier) && identifier.Length > 1 && identifier[0] == '0')) return false;
        version = new(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value, prerelease);
        return true;
    }

    public int CompareTo(SourceSemanticVersion? other)
    {
        if (other is null) return 1;
        var result = CompareNumeric(Major, other.Major);
        if (result != 0) return result;
        result = CompareNumeric(Minor, other.Minor);
        if (result != 0) return result;
        result = CompareNumeric(Patch, other.Patch);
        if (result != 0) return result;
        if (Prerelease.Length == 0 || other.Prerelease.Length == 0)
            return Prerelease.Length == other.Prerelease.Length ? 0 : Prerelease.Length == 0 ? 1 : -1;
        for (var index = 0; index < Math.Min(Prerelease.Length, other.Prerelease.Length); index++)
        {
            result = CompareIdentifier(Prerelease[index], other.Prerelease[index]);
            if (result != 0) return result;
        }
        return Prerelease.Length.CompareTo(other.Prerelease.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = IsNumeric(left);
        var rightNumeric = IsNumeric(right);
        if (leftNumeric && rightNumeric) return CompareNumeric(left, right);
        if (leftNumeric != rightNumeric) return leftNumeric ? -1 : 1;
        return string.CompareOrdinal(left, right);
    }

    private static int CompareNumeric(string left, string right) =>
        left.Length != right.Length ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);

    private static bool IsNumeric(string value) => value.All(char.IsAsciiDigit);

    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?(?:\\+([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?$")]
    private static partial Regex SemanticVersionPattern();
}
