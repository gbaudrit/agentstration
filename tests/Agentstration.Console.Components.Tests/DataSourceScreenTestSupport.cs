using Agentstration.Web.Components;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

internal static class DataSourceScreenTestSupport
{
    public static void Configure(BunitContext context)
    {
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        var uri = context.Services.GetRequiredService<NavigationManager>().Uri.Split('#')[0];
        foreach (var path in new[] { "overview", "definition/identity", "definition/configuration", "definition/contracts", "definition/treatments", "discovery", "acquisitions", "content", "queries", "tools", "access", "yaml" })
            module.Setup<string>("navigate", i => i.Arguments[0] is string url && url.EndsWith("#" + path, StringComparison.Ordinal)).SetResult(uri + "#" + path);
    }

    public static void Select<T>(IRenderedComponent<T> rendered, string path) where T : class, IComponent =>
        rendered.InvokeAsync(() => rendered.FindComponent<ResourceNavigation>().Instance.ObserveLocation(
            rendered.Find($"[data-resource-section='{path}']").GetAttribute("href")!)).GetAwaiter().GetResult();
}
