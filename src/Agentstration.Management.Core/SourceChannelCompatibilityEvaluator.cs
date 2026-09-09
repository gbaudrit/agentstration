using Agentstration.Management.Abstractions;

namespace Agentstration.Management.Core;

public sealed class SourceChannelCompatibilityEvaluator(IAgentstrationVersionProvider versions)
{
    public SourceChannelCompatibilityView Evaluate(SourceChannelDefinition channel)
    {
        var bounds = channel.Compatibility?.Agentstration;
        if (bounds is null || !SourceSemanticVersion.TryParse(bounds.MinVersion, out var minimum)
            || bounds.MaxVersionExclusive is { } maximumValue && !SourceSemanticVersion.TryParse(maximumValue, out _))
            return new(SourceChannelCompatibilityStatus.CompatibilityUnknown, versions.CurrentVersion,
                bounds,
                "source_compatibility_definition_invalid", "The Channel compatibility interval is invalid.");

        var runningValue = versions.CurrentVersion;
        if (!SourceSemanticVersion.TryParse(runningValue, out var running))
            return new(SourceChannelCompatibilityStatus.CompatibilityUnknown, runningValue, bounds,
                "source_running_version_unknown", "The running Agentstration version is unavailable or is not a Semantic Version.");
        if (running.CompareTo(minimum) < 0)
            return new(SourceChannelCompatibilityStatus.Incompatible, runningValue, bounds,
                "source_running_version_below_minimum", $"Agentstration {runningValue} is older than the inclusive minimum {bounds.MinVersion}.");
        if (bounds.MaxVersionExclusive is { } maximumText
            && SourceSemanticVersion.TryParse(maximumText, out var maximum)
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

    private static SourceValidationException Invalid(string code, string message) => new(code, message);
}
