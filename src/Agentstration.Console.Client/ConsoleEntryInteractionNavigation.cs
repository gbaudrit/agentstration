using Agentstration.Resources;

namespace Agentstration.Web.Console;

public static class ConsoleEntryInteractionNavigation
{
    public static string Build(
        Guid workspaceId,
        ResourceNamespace @namespace,
        string entryName,
        string? initialQuery = null,
        Guid? interactionId = null)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("A Console Entry interaction requires an owning Workspace.", nameof(workspaceId));
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);

        var path = $"/entry-interactions/{workspaceId:D}/{Escape(@namespace.Value)}/{Escape(entryName)}";
        var query = new List<string>();
        if (initialQuery is not null) query.Add($"query={Escape(initialQuery)}");
        if (interactionId is not null) query.Add($"interaction={interactionId:D}");
        return query.Count == 0 ? path : $"{path}?{string.Join('&', query)}";
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
