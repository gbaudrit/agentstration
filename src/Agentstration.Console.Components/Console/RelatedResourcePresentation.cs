using Agentstration.Resources;
using Agentstration.Web.Components;

namespace Agentstration.Web.Console;

public static class RelatedResourcePresentation
{
    public static string? Href(IServiceProvider services, string kind, string name, ResourceNamespace ns,
        ResourceScopeRef owner, string section = "overview")
    {
        var path = kind switch
        {
            "Tool" => "/tools",
            "ToolSet" or "ToolSetVersion" => "/tools/sets",
            "ToolProvider" => "/tools/providers",
            "ToolDefinition" or "ToolDefinitionVersion" => "/tools/definitions",
            "DataSourceProfile" => "/data-source-profiles",
            _ => null
        };
        if (kind == "Flow")
        {
            var escaped = Uri.EscapeDataString(name.Split('@')[0]);
            var flowPath = ns.IsDefault ? $"/flows/{escaped}" : $"/namespaces/{Uri.EscapeDataString(ns.Value)}/flows/{escaped}";
            return ConsoleResourceUrls.WithOwnerScope(ResourceOwnershipContext.PreserveNavigation(flowPath, services), owner) + "#" + section;
        }
        return path is null ? null : ResourceOwnershipContext.Resource(services, path, name.Split('@')[0], ns, owner) + "#" + section;
    }
}
