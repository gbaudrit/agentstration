using Agentstration.Resources;

namespace Agentstration.Web.Console;

public static class ConsoleEntryInteractionNavigation
{
    public static string Build(
        Guid workspaceId,
        ResourceNamespace @namespace,
        string entryName,
        string? initialQuery = null)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("A Console Entry interaction requires an owning Workspace.", nameof(workspaceId));
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);

        var path = $"/entry-interactions/{workspaceId:D}/{Escape(@namespace.Value)}/{Escape(entryName)}";
        return initialQuery is null ? path : $"{path}?query={Escape(initialQuery)}";
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
