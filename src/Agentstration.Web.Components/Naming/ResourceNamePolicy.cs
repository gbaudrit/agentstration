using System.Globalization;
using System.Text;

namespace Agentstration.Web.Components.Naming;

public sealed record ResourceNamePolicy(
    string Id,
    int MaximumLength,
    char Separator,
    Func<char, bool> IsAllowed,
    bool Lowercase = true,
    bool Transliterate = true,
    IReadOnlySet<string>? ReservedValues = null)
{
    public static ResourceNamePolicy Generic { get; } = Ascii("generic", 128, '-', allowDot: true, allowUnderscore: true);
    public static ResourceNamePolicy Flow { get; } = Ascii("flow", 128, '-', allowUnderscore: true);
    public static ResourceNamePolicy Entry { get; } = Ascii("entry", 128, '-', allowUnderscore: true);
    public static ResourceNamePolicy Dashboard { get; } = Ascii("dashboard", 128, '-', allowUnderscore: true);
    public static ResourceNamePolicy Parameter { get; } = Ascii("parameter", 128, '-', allowDot: true);
    public static ResourceNamePolicy Trigger { get; } = Ascii("trigger", 63, '-');
    public static ResourceNamePolicy SourceRegistry { get; } = Ascii("source-registry", 128, '-');
    public static ResourceNamePolicy Tenant { get; } = Ascii("tenant", 64, '-');
    public static ResourceNamePolicy Workspace { get; } = Ascii("workspace", 64, '-');
    public static ResourceNamePolicy Pack { get; } = Ascii("pack", 60, '-');

    private static ResourceNamePolicy Ascii(string id, int maximumLength, char separator, bool allowDot = false, bool allowUnderscore = false) =>
        new(id, maximumLength, separator, character => char.IsAsciiLetterOrDigit(character)
            || allowDot && character == '.'
            || allowUnderscore && character == '_');
}

public static class ResourceNameDerivation
{
    public static string Derive(string? displayName, ResourceNamePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(displayName)) return string.Empty;

        var source = policy.Transliterate
            ? displayName.Normalize(NormalizationForm.FormD)
            : displayName.Normalize(NormalizationForm.FormC);
        var result = new StringBuilder(Math.Min(source.Length, policy.MaximumLength));
        var separatorPending = false;

        foreach (var sourceCharacter in source)
        {
            if (policy.Transliterate && CharUnicodeInfo.GetUnicodeCategory(sourceCharacter) == UnicodeCategory.NonSpacingMark)
                continue;

            var character = policy.Lowercase ? char.ToLowerInvariant(sourceCharacter) : sourceCharacter;
            if (result.Length == 0 && !char.IsAsciiLetterOrDigit(character))
                continue;
            if (policy.IsAllowed(character))
            {
                if (separatorPending && result.Length > 0 && result[^1] != policy.Separator && result.Length < policy.MaximumLength)
                    result.Append(policy.Separator);
                separatorPending = false;
                if (result.Length < policy.MaximumLength)
                    result.Append(character);
            }
            else
            {
                separatorPending = result.Length > 0;
            }

            if (result.Length >= policy.MaximumLength)
                break;
        }

        while (result.Length > 0 && result[^1] == policy.Separator)
            result.Length--;

        var derived = result.ToString().Normalize(NormalizationForm.FormC);
        return policy.ReservedValues?.Contains(derived) == true ? string.Empty : derived;
    }
}
