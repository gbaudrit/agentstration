using System.Globalization;
using Agentstration.Web.Components;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class ResourceNavigationTests
{
    [TestMethod]
    [DataRow("en-US", "Close sections")]
    [DataRow("fr-FR", "Fermer les sections")]
    public async Task DirectChildSelectionAndDrawerShareHierarchyAndLocalizedControls(string culture, string close)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            using var context = Context("#definition/tools");
            var selected = new List<string?>();
            var cut = context.Render<ResourceNavigation>(p => p.Add(c => c.Items, Items()).Add(c => c.DefaultPath, "overview")
                .Add(c => c.SelectionChanged, (string? path) => selected.Add(path)));
            cut.WaitForAssertion(() => Assert.AreEqual("definition/tools", selected.Last()));
            Assert.AreEqual("page", cut.Find(".resource-navigation-rail a[data-resource-section='definition/tools']").GetAttribute("aria-current"));
            Assert.IsFalse(cut.Find(".resource-navigation-children").HasAttribute("hidden"));
            await cut.Find(".resource-navigation-trigger").ClickAsync();
            Assert.IsNotNull(cut.Find($"dialog button[aria-label='{close}']"));
            await cut.InvokeAsync(() => cut.Instance.NavigateSection("yaml"));
            Assert.AreEqual("yaml", cut.Instance.CurrentPath);
            Assert.IsFalse(cut.FindComponent<ModalFrame>().Instance.IsOpen);
            Assert.AreEqual("yaml", selected.Last());
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [TestMethod]
    [DataRow("#unknown")]
    [DataRow("#definition/secret")]
    [DataRow("#disabled")]
    [DataRow("#definition%2Ftools")]
    [DataRow("#definition/tools/extra")]
    [DataRow("#OVERVIEW")]
    public void InvalidOrUnavailableDestinationsFallBackWithoutLeakingHiddenLabels(string fragment)
    {
        using var context = Context(fragment);
        var cut = context.Render<ResourceNavigation>(p => p.Add(c => c.Items, Items()).Add(c => c.DefaultPath, "overview"));
        cut.WaitForAssertion(() => Assert.AreEqual("overview", cut.Instance.CurrentPath));
        Assert.DoesNotContain("Confidential section", cut.Markup);
        Assert.AreEqual(context.Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<ResourceNavigationStrings>>()["Unavailable"].Value, cut.Find(".resource-navigation-feedback").TextContent);
        var uri = new Uri((string)context.JSInterop.Invocations.Last(call => call.Identifier == "navigate").Arguments[0]!);
        Assert.AreEqual("#overview", uri.Fragment);
        Assert.Contains("scopeRef=%2Ftenants%2Fowner", uri.Query);
        Assert.Contains("context=%2Finstance", uri.Query);
        Assert.Contains("namespace=demo", uri.Query);
        Assert.DoesNotContain("view=", uri.Query);
        Assert.DoesNotContain("tab=", uri.Query);
    }

    [TestMethod]
    public async Task HashChangesIgnoreObsoleteSelectorsAndPreserveTheOwningEditor()
    {
        using var context = Context("#overview");
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var cut = context.Render<Owner>();
        cut.WaitForAssertion(() => Assert.AreEqual("overview", cut.Instance.Selected));
        await cut.Find("input").InputAsync("unsaved form");
        await cut.Find("textarea").InputAsync("invalid: [ raw YAML");
        var owner = cut.Instance;
        await cut.InvokeAsync(() => navigation.NavigateTo(navigation.Uri.Split('#')[0] + "#definition/tools"));
        cut.WaitForAssertion(() => Assert.AreEqual("definition/tools", owner.Selected));
        await cut.InvokeAsync(() => navigation.NavigateTo(navigation.Uri.Split('#')[0] + "#yaml"));
        cut.WaitForAssertion(() => Assert.AreEqual("yaml", owner.Selected));
        Assert.AreSame(owner, cut.Instance);
        Assert.AreEqual("unsaved form", owner.Draft);
        Assert.AreEqual("invalid: [ raw YAML", owner.Yaml);
        Assert.AreEqual(1, owner.Initializations);
        await cut.InvokeAsync(() => cut.FindComponent<ResourceNavigation>().Instance.NavigateSection("definition/secret"));
        Assert.AreEqual("yaml", owner.Selected);
    }

    [TestMethod]
    public async Task GroupingParentsResolveToAllowedChildrenAndExplicitToggleDoesNotNavigate()
    {
        using var context = Context("#definition");
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        var cut = context.Render<ResourceNavigation>(p => p.Add(c => c.Items, Items()));
        cut.WaitForAssertion(() => Assert.AreEqual("definition/tools", cut.Instance.CurrentPath));
        var location = navigation.Uri;
        await cut.Find(".resource-navigation-rail button[aria-controls]").ClickAsync();
        Assert.AreEqual(location, navigation.Uri);
        Assert.IsTrue(cut.Find(".resource-navigation-children").HasAttribute("hidden"));
        await cut.Find(".resource-navigation-rail button[aria-controls]").ClickAsync();
        Assert.IsFalse(cut.Find(".resource-navigation-children").HasAttribute("hidden"));
    }

    [TestMethod]
    public async Task InitiallyExpandedGroupsKeepTheCallersCollapseChoiceAcrossRenders()
    {
        using var context = Context("#overview");
        var cut = context.Render<ResourceNavigation>(p => p.Add(c => c.Items, Items()).Add(c => c.ExpandGroupsInitially, true));
        Assert.IsFalse(cut.Find(".resource-navigation-children").HasAttribute("hidden"));
        await cut.Find(".resource-navigation-rail button[aria-controls]").ClickAsync();
        cut.Render();
        Assert.IsTrue(cut.Find(".resource-navigation-children").HasAttribute("hidden"));
        Assert.AreEqual("overview", cut.Instance.CurrentPath);
    }

    [TestMethod]
    public void PermissionChangesChooseAnAllowedDefaultAndNoAllowedViewSelectsNothing()
    {
        using var context = Context("#yaml");
        var items = Items();
        var cut = context.Render<ResourceNavigation>(p => p.Add(c => c.Items, items));
        cut.WaitForAssertion(() => Assert.AreEqual("yaml", cut.Instance.CurrentPath));
        cut.Render(p => p.Add(c => c.Items, items.Select(item => item with { Visible = item.Path == "overview" }).ToArray()));
        cut.WaitForAssertion(() => Assert.AreEqual("overview", cut.Instance.CurrentPath));
        cut.Render(p => p.Add(c => c.Items, items.Select(item => item with { Visible = false }).ToArray()));
        Assert.IsNull(cut.Instance.CurrentPath);
        Assert.IsEmpty(cut.FindAll("a"));
        Assert.Contains(context.Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<ResourceNavigationStrings>>()["Empty"].Value, cut.Markup);
    }

    [TestMethod]
    public async Task ResourceOrContextChangesAreNotNormalizedByTheOldNavigationInstance()
    {
        using var context = Context("#overview");
        var nav = context.Services.GetRequiredService<NavigationManager>();
        var cut = context.Render<ResourceNavigation>(p => p.Add(c => c.Items, Items()));
        cut.WaitForAssertion(() => Assert.AreEqual("overview", cut.Instance.CurrentPath));
        var destination = "http://localhost/agents/another?context=%2Ftenants%2Fother#yaml";
        await cut.InvokeAsync(() => nav.NavigateTo(destination));
        await cut.InvokeAsync(() => cut.Instance.NavigateSection("definition/tools"));
        Assert.AreEqual(destination, nav.Uri);
        Assert.AreEqual("overview", cut.Instance.CurrentPath);
        await cut.InvokeAsync(cut.Instance.DisposeAsync);
        await cut.InvokeAsync(() => nav.NavigateTo("http://localhost/elsewhere#definition/tools"));
        Assert.AreEqual("overview", cut.Instance.CurrentPath);
    }

    [TestMethod]
    [DataRow("duplicate")]
    [DataRow("depth")]
    [DataRow("missing-parent")]
    [DataRow("empty-group")]
    [DataRow("invalid-icon")]
    [DataRow("invalid-id")]
    public void InvalidConfigurationsFailDeterministically(string variant)
    {
        using var context = Context("#overview");
        ResourceNavigationItem[] items = variant switch
        {
            "duplicate" => [new("same", "First", "home"), new("same", "Second", "home")],
            "depth" => [new("a/b/c", "Too deep", "home")],
            "missing-parent" => [new("a/b", "Orphan", "home")],
            "empty-group" => [new("a", "Empty", "home") { Navigable = false }],
            "invalid-icon" => [new("a", "Icon", "not-a-catalog-icon")],
            _ => [new("invalid id", "Invalid", "home")]
        };
        Assert.Throws<ArgumentException>(() => context.Render<ResourceNavigation>(p => p.Add(c => c.Items, items)));
    }

    [TestMethod]
    public async Task DisposalWhileObservationIsInitializingReleasesLateRegistration()
    {
        var runtime = new DelayedRuntime();
        using var context = Context("#overview", runtime);
        var cut = context.Render<ResourceNavigation>(p => p.Add(c => c.Items, Items()));
        var registration = new LateRegistration();
        await cut.InvokeAsync(cut.Instance.DisposeAsync);
        runtime.Module.Ready.SetResult(registration);
        cut.WaitForAssertion(() => Assert.AreEqual(1, registration.Releases));
        await cut.InvokeAsync(cut.Instance.DisposeAsync);
        Assert.AreEqual(1, registration.Releases);
    }
    private sealed class DelayedRuntime : IJSRuntime
    {
        public DelayedModule Module { get; } = new();
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, CancellationToken.None, args);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => ValueTask.FromResult((T)(object)Module);
    }
    private sealed class DelayedModule : IJSObjectReference
    {
        public TaskCompletionSource<IJSObjectReference> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, CancellationToken.None, args);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => new(WaitAsync<T>());
        private async Task<T> WaitAsync<T>() => (T)await Ready.Task;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class LateRegistration : IJSObjectReference
    {
        public int Releases { get; private set; }
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, CancellationToken.None, args);
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
        {
            Assert.AreEqual("dispose", identifier); Releases++;
            return ValueTask.FromResult(default(T)!);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [TestMethod]
    public void DirectPreviewFragmentIsOwnedByTheCollectionWithoutSectionCanonicalization()
    {
        const string fragment = "#preview/Tool/%2Finstance/default/example";
        using var context = Context(fragment);
        var cut = context.Render<ResourceNavigation>(p => p.Add(c => c.Items, Items()).Add(c => c.DefaultPath, "overview"));
        Assert.AreEqual("overview", cut.Instance.CurrentPath);
        Assert.AreEqual(fragment, new Uri(context.Services.GetRequiredService<NavigationManager>().Uri).Fragment);
        Assert.IsEmpty(context.JSInterop.Invocations.Where(call => call.Identifier == "navigate"));
        Assert.IsEmpty(cut.FindAll(".resource-navigation-feedback"));
    }
    [TestMethod]
    public async Task EmbeddedPreviewKeepsThePreviouslySelectedNativeSectionMounted()
    {
        using var context = Context("#definition/tools");
        var cut = context.Render<ResourceNavigation>(p => p.Add(c => c.Items, Items()).Add(c => c.DefaultPath, "overview"));
        var nav = context.Services.GetRequiredService<NavigationManager>();
        var before = context.JSInterop.Invocations.Count(call => call.Identifier == "navigate");
        await cut.InvokeAsync(() => nav.NavigateTo(nav.Uri.Split('#')[0] + "#preview/Tool/%2Finstance/default/example"));
        Assert.AreEqual("definition/tools", cut.Instance.CurrentPath);
        Assert.AreEqual(before, context.JSInterop.Invocations.Count(call => call.Identifier == "navigate"));
        Assert.IsEmpty(cut.FindAll(".resource-navigation-feedback"));
    }

    private static ResourceNavigationItem[] Items() =>
    [
        new("overview", "Overview", "home"),
        new("definition", "Definition", "settings") { Navigable = false },
        new("definition/tools", "Tools", "tool"),
        new("definition/secret", "Confidential section", "lock") { Visible = false },
        new("disabled", "Disabled", "clock") { Enabled = false },
        new("yaml", "YAML", "code")
    ];
    private static BunitContext Context(string fragment, IJSRuntime? runtime = null)
    {
        var context = new BunitContext();
        context.Services.AddLocalization(o => o.ResourcesPath = "Resources");
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        if (runtime is not null) context.Services.AddSingleton(runtime);
        var nav = context.Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("http://localhost/agents/example?scopeRef=%2Ftenants%2Fowner&context=%2Finstance&namespace=demo&view=legacy&tab=ignored" + fragment);
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/resource-navigation.js");
        foreach (var path in new[] { "overview", "definition/tools", "yaml" })
        {
            var target = "http://localhost/agents/example?scopeRef=%2Ftenants%2Fowner&context=%2Finstance&namespace=demo#" + path;
            module.Setup<string>("navigate", invocation => Equals(invocation.Arguments[0], target)).SetResult(target);
        }
        context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/dialog.js");
        return context;
    }
    public sealed class Owner : ComponentBase
    {
        public string? Selected { get; private set; }
        public string Draft { get; private set; } = "";
        public string Yaml { get; private set; } = "";
        public int Initializations { get; private set; }
        protected override void OnInitialized() => Initializations++;
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder b)
        {
            b.OpenComponent<ResourceNavigation>(0); b.AddAttribute(1, "Items", Items());
            b.AddAttribute(2, "SelectionChanged", EventCallback.Factory.Create<string?>(this, value => Selected = value)); b.CloseComponent();
            b.OpenElement(3, "input"); b.AddAttribute(4, "value", Draft); b.AddAttribute(5, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, e => Draft = e.Value?.ToString() ?? "")); b.CloseElement();
            b.OpenElement(6, "textarea"); b.AddAttribute(7, "value", Yaml); b.AddAttribute(8, "oninput", EventCallback.Factory.Create<ChangeEventArgs>(this, e => Yaml = e.Value?.ToString() ?? "")); b.CloseElement();
        }
    }
}
