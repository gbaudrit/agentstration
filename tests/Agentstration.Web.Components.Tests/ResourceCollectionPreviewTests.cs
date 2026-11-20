using Agentstration.Resources;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class ResourceCollectionPreviewTests
{
    private static readonly ScopedResourceAddress Target = ScopedResourceAddress.Create(ResourceScopeRef.Workspace(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001")), new("team.demo"), "Agent", "same-name");

    [TestMethod]
    public async Task ServerNavigationShowsTheSelectionBeforeTheBrowserAcknowledgesTheFragment()
    {
        using var context = Context();
        var navigation = new DeferredNavigation();
        context.Services.AddSingleton<NavigationManager>(navigation);
        var cut = Render(context);
        await cut.InvokeAsync(() => cut.Instance.Open(Target));
        Assert.AreEqual(Target.ToString(), cut.Find("p").TextContent);
        cut.Render();
        Assert.AreEqual(Target.ToString(), cut.Find("p").TextContent);
        await cut.InvokeAsync(navigation.Acknowledge);
        Assert.AreEqual(Target.ToString(), cut.Find("p").TextContent);
    }

    [TestMethod]
    public async Task ReturningToTheAnchoredCollectionReopensTheExactPreviewWithoutChangingTheQuery()
    {
        using var context = Context();
        var nav = context.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/agents?context=%2Finstance&top=20");
        var cut = Render(context);
        await cut.InvokeAsync(() => cut.Instance.Open(Target));
        var source = nav.Uri;
        Assert.Contains("#preview/Agent/%2Fworkspaces%2F", source);
        Assert.Contains("context=%2Finstance&top=20", source);
        cut.WaitForAssertion(() => Assert.AreEqual(Target.ToString(), cut.Find("p").TextContent));
        nav.NavigateTo("/agents/same-name#overview");
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("dialog")));
        nav.NavigateTo(source);
        cut.WaitForAssertion(() => Assert.AreEqual(Target.ToString(), cut.Find("p").TextContent));
        await cut.InvokeAsync(cut.Instance.Close);
        Assert.AreEqual("", new Uri(nav.Uri).Fragment);
        Assert.Contains("context=%2Finstance&top=20", nav.Uri);
    }

    [TestMethod]
    public async Task ANewCollectionInstanceRestoresThePreviewFromItsAddressOutsideTheLoadedWindow()
    {
        using var context = Context();
        var nav = context.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/agents");
        var original = Render(context);
        await original.InvokeAsync(() => original.Instance.Open(Target));
        var source = nav.Uri;
        await context.DisposeComponentsAsync();
        nav.NavigateTo(source);
        var restored = Render(context);
        Assert.AreEqual(Target.ToString(), restored.Find("p").TextContent);
    }

    [TestMethod]
    public async Task BrowserBackAndForwardWithinTheCollectionFollowTheFragment()
    {
        using var context = Context();
        var nav = context.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/agents");
        var cut = Render(context);
        await cut.InvokeAsync(() => cut.Instance.Open(Target));
        var source = nav.Uri;
        nav.NavigateTo("/agents");
        cut.WaitForAssertion(() => Assert.IsEmpty(cut.FindAll("dialog")));
        nav.NavigateTo(source);
        cut.WaitForAssertion(() => Assert.HasCount(1, cut.FindAll("dialog")));
    }

    [TestMethod]
    public async Task ContextReplacementClearsTheOpenPreviewAndItsFragment()
    {
        using var context = Context();
        var nav = context.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/agents");
        var cut = Render(context);
        await cut.InvokeAsync(() => cut.Instance.Open(Target));
        cut.Render(p => p.Add(c => c.ContextKey, "another-principal-or-scope"));
        Assert.IsEmpty(cut.FindAll("dialog"));
        Assert.AreEqual("", new Uri(nav.Uri).Fragment);
    }

    [TestMethod]
    [DataRow("#preview/Flow/%2Finstance/default/flow")]
    [DataRow("#preview/Agent/unknown/default/agent")]
    [DataRow("#preview/Agent/%2Finstance/default")]
    [DataRow("#overview")]
    public void InvalidOrOtherFamilyFragmentsDoNotMountContent(string fragment)
    {
        using var context = Context();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/agents" + fragment);
        var cut = Render(context);
        Assert.IsEmpty(cut.FindAll("dialog, p"));
    }

    [TestMethod]
    public void DisabledPreviewDoesNotMountItsContentFromAnIncomingAnchor()
    {
        using var context = Context();
        context.Services.GetRequiredService<NavigationManager>().NavigateTo("/agents#preview/Agent/%2Finstance/default/agent");
        var cut = context.Render<ResourceCollectionPreview>(p => p.Add(c => c.ResourceKind, "Agent").Add(c => c.Enabled, false)
            .Add(c => c.ChildContent, _ => builder => builder.AddMarkupContent(0, "<p>Authorized content</p>")));
        Assert.IsEmpty(cut.FindAll("dialog, p"));
        cut.Render(p => p.Add(c => c.Enabled, true));
        Assert.AreEqual("Authorized content", cut.Find("p").TextContent);
    }

    private static BunitContext Context()
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        return context;
    }
    private static IRenderedComponent<ResourceCollectionPreview> Render(BunitContext context) => context.Render<ResourceCollectionPreview>(p => p
        .Add(c => c.ResourceKind, "Agent").Add(c => c.ContextKey, "principal|scope")
        .Add(c => c.ChildContent, address => builder => { builder.OpenElement(0, "p"); builder.AddContent(1, address.ToString()); builder.CloseElement(); }));

    private sealed class DeferredNavigation : NavigationManager
    {
        private string? pending;
        public DeferredNavigation() => Initialize("https://agentstration.test/", "https://agentstration.test/agents");
        protected override void NavigateToCore(string uri, bool forceLoad) => pending = ToAbsoluteUri(uri).AbsoluteUri;
        public void Acknowledge()
        {
            Uri = pending!;
            NotifyLocationChanged(false);
        }
    }
}
