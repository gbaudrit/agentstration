using System.Text.RegularExpressions;
using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Core;

public sealed partial class SourceChannelCompatibilityEvaluator(IAgentstrationVersionProvider versions)
{
    public static void Validate(SourceCompatibilityBounds? bounds, string channel)
    {
        if (bounds is null || string.IsNullOrWhiteSpace(bounds.MinVersion))
            throw Invalid("source_channel_compatibility_missing", $"Channel '{channel}' must declare compatibility.agentstration.minVersion.");
        if (!SemanticVersion.TryParse(bounds.MinVersion, out var minimum))
            throw Invalid("source_compatibility_version_invalid", $"Channel '{channel}' minVersion '{bounds.MinVersion}' is not a valid Semantic Version.");
        if (bounds.MaxVersionExclusive is null) return;
        if (!SemanticVersion.TryParse(bounds.MaxVersionExclusive, out var maximum))
            throw Invalid("source_compatibility_version_invalid", $"Channel '{channel}' maxVersionExclusive '{bounds.MaxVersionExclusive}' is not a valid Semantic Version.");
        if (minimum.CompareTo(maximum) >= 0)
            throw Invalid("source_compatibility_interval_invalid", $"Channel '{channel}' maxVersionExclusive must be greater than minVersion.");
    }

    public SourceChannelCompatibilityView Evaluate(SourceChannelDefinition channel)
    {
        var bounds = channel.Compatibility?.Agentstration;
        if (bounds is null || !SemanticVersion.TryParse(bounds.MinVersion, out var minimum)
            || bounds.MaxVersionExclusive is { } maximumValue && !SemanticVersion.TryParse(maximumValue, out _))
            return new(SourceChannelCompatibilityStatus.CompatibilityUnknown, versions.CurrentVersion,
                bounds,
                "source_compatibility_definition_invalid", "The Channel compatibility interval is invalid.");

        var runningValue = versions.CurrentVersion;
        if (!SemanticVersion.TryParse(runningValue, out var running))
            return new(SourceChannelCompatibilityStatus.CompatibilityUnknown, runningValue, bounds,
                "source_running_version_unknown", "The running Agentstration version is unavailable or is not a Semantic Version.");
        if (running.CompareTo(minimum) < 0)
            return new(SourceChannelCompatibilityStatus.Incompatible, runningValue, bounds,
                "source_running_version_below_minimum", $"Agentstration {runningValue} is older than the inclusive minimum {bounds.MinVersion}.");
        if (bounds.MaxVersionExclusive is { } maximumText
            && SemanticVersion.TryParse(maximumText, out var maximum)
            && running.CompareTo(maximum) >= 0)
            return new(SourceChannelCompatibilityStatus.Incompatible, runningValue, bounds,
                "source_running_version_at_or_above_maximum", $"Agentstration {runningValue} is at or above the exclusive maximum {maximumText}.");
        return new(SourceChannelCompatibilityStatus.Compatible, runningValue, bounds, null, null);
    }

    public void RequireCompatible(SourceChannelDefinition channel)
    {
        var evaluation = Evaluate(channel);
        if (evaluation.Status == SourceChannelCompatibilityStatus.Compatible) return;
        var code = evaluation.Status == SourceChannelCompatibilityStatus.Incompatible
            ? "source_channel_incompatible"
            : "source_channel_compatibility_unknown";
        throw Invalid(code, $"Channel '{channel.Name}' cannot be used: {evaluation.Reason}");
    }

    private sealed record SemanticVersion(string Major, string Minor, string Patch, string[] Prerelease) : IComparable<SemanticVersion>
    {
        public static bool TryParse(string? value, out SemanticVersion version)
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

        public int CompareTo(SemanticVersion? other)
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
    }

    [GeneratedRegex("^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?(?:\\+([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?$")]
    private static partial Regex SemanticVersionPattern();

    private static SourceValidationException Invalid(string code, string message) => new(code, message);
}
