namespace Agentstration.Aep.Abstractions;

public sealed record AepToolSetContribution(string Id, string DisplayName, string Version,
    IReadOnlyList<AepToolSetMember> Members, string? Description = null);
public sealed record AepToolSetMember(string ToolId, string Capability, string Route = "default");

public static class AepToolSetDescriptorValidator
{
    public const string Capability = "aep.tool-sets";
    public const string Version = "1.0";

    public static IReadOnlyList<string> Validate(AepManifest descriptor)
    {
        var errors = new List<string>();
        var groups = descriptor.Contributions.ToolSets ?? [];
        var declared = descriptor.Capabilities.TryGetValue(Capability, out var capability);
        if (groups.Count > 32) errors.Add("ToolSet contribution count exceeds 32.");
        if (groups.Count > 0 && !declared) errors.Add("ToolSet contributions need the aep.tool-sets capability.");
        if (declared && (capability!.Version != Version || groups.Count == 0)) errors.Add("The aep.tool-sets capability requires version 1.0 and at least one ToolSet.");
        var tools = (descriptor.Contributions.Tools ?? []).Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in groups.Take(32))
        {
            if (group is null) { errors.Add("ToolSet contributions cannot be null."); continue; }
            if (!Token(group.Id) || group.Id.Length > 64 || !ids.Add(group.Id)) errors.Add("ToolSet ids must be valid and distinct, with at most 64 characters.");
            if (string.IsNullOrWhiteSpace(group.DisplayName) || group.DisplayName.Length > 200 || group.Description?.Length > 2000)
                errors.Add("ToolSet presentation exceeds its bounded contract.");
            if (string.IsNullOrWhiteSpace(group.Version) || group.Version.Length > 64 || group.Version.Any(value => !char.IsLetterOrDigit(value) && value is not '.' and not '-'))
                errors.Add("ToolSet version is invalid.");
            if (group.Members is not { Count: > 0 and <= 128 }) { errors.Add("ToolSets require 1 to 128 members."); continue; }
            var members = new HashSet<string>(StringComparer.Ordinal); var routes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in group.Members)
                if (member is null || !Token(member.ToolId) || !tools.Contains(member.ToolId) || !members.Add(member.ToolId)
                    || !Token(member.Capability) || !Token(member.Route) || !routes.Add(member.Capability + "|" + member.Route))
                    errors.Add("ToolSet members must select distinct advertised Tools and distinct valid capability/route pairs.");
        }
        return errors;
    }

    private static bool Token(string? value) => value is { Length: > 0 and <= 128 }
        && value.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_');
}
