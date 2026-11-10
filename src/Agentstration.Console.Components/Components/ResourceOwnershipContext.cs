using Agentstration.Resources;
using Agentstration.Web.Components.State;

namespace Agentstration.Web.Components;

internal static class ResourceOwnershipContext
{
    public static string Resource(IServiceProvider services, string collection, string name,
        ResourceNamespace @namespace, ResourceScopeRef? ownerScope) =>
        Agentstration.Web.Console.ConsoleResourceUrls.Resource(collection, name, @namespace, ownerScope, TryGetCurrent(services));

    public static string PreserveNavigation(string uri, IServiceProvider services) =>
        services.GetService(typeof(IConsoleNavigationContext)) is IConsoleNavigationContext navigation
            ? navigation.AddToUri(uri)
            : uri;

    public static bool CanManage(ResourceScopeRef? ownerScope, IServiceProvider services)
    {
        var navigationContext = services.GetService(typeof(IConsoleNavigationContext)) as IConsoleNavigationContext;
        return navigationContext is null || CanManage(ownerScope, navigationContext);
    }

    public static ResourceScopeRef? TryGetCurrent(IServiceProvider services) =>
        services.GetService(typeof(IConsoleNavigationContext)) is IConsoleNavigationContext navigationContext
            ? TryGetCurrent(navigationContext)
            : null;

    public static bool CanManage(ResourceScopeRef? ownerScope, IConsoleNavigationContext navigationContext) =>
        ownerScope is not null && TryGetCurrent(navigationContext) == ownerScope;

    public static ResourceScopeRef? TryGetCurrent(IConsoleNavigationContext navigationContext)
    {
        if (string.IsNullOrWhiteSpace(navigationContext.ScopeRef)) return null;
        try { return ResourceScopeRef.Parse(navigationContext.ScopeRef); }
        catch (ArgumentException) { return null; }
    }
}
