using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class InheritedResourceNoticeTests
{
    private static readonly ResourceScopeRef WorkspaceScope = ResourceScopeRef.Workspace(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

    [TestMethod]
    public void InheritedResourceShowsReadOnlyNoticeAndOwnerContextLink()
    {
        using var context = CreateContext(ResourceScopeRef.Instance.Value);
        context.Services.GetRequiredService<NavigationManager>()
            .NavigateTo("http://localhost/agents/shared?context=%2Finstance");

        var rendered = context.Render<InheritedResourceNotice>(parameters => parameters
            .Add(component => component.OwnerScopeRef, WorkspaceScope));

        Assert.IsNotNull(rendered.Find("[data-testid='inherited-resource-notice']"));
        var link = rendered.Find("a");
        StringAssert.Contains(link.GetAttribute("href"), Uri.EscapeDataString(WorkspaceScope.Value));
    }

    [TestMethod]
    public void OwnerContextDoesNotShowInheritedNotice()
    {
        using var context = CreateContext(WorkspaceScope.Value);

        var rendered = context.Render<InheritedResourceNotice>(parameters => parameters
            .Add(component => component.OwnerScopeRef, WorkspaceScope));

        Assert.AreEqual(string.Empty, rendered.Markup);
    }

    private static BunitContext CreateContext(string scopeRef)
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConsoleNavigationContext>(new StubNavigationContext(scopeRef));
        return context;
    }

    private sealed class StubNavigationContext(string scopeRef) : IConsoleNavigationContext
    {
        public string? ScopeRef => scopeRef;
        public string AddToUri(string uri) => uri;
    }
}
