using Agentstration.Web.Components.State;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Components.Tests;

public sealed partial class MainLayoutNavigationTests
{
    private static string SwitchTarget => $"/workspaces/{SwitchingContextProvider.WorkspaceId:D}";

    [TestMethod]
    public async Task SwitchingUsesTheBrowsersCurrentAnchorWhenBlazorStillHasThePreviousOne()
    {
        var resolver = new SwitchResolver(request => Task.FromResult(new ConsoleContextSwitchDestination(request.FallbackLocation, true)));
        using var context = SwitchContext(resolver);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/triggers/schedule?context=%2Finstance#overview");
        var browserLocation = navigation.Uri.Replace("#overview", "#yaml", StringComparison.Ordinal);
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/context-selector.js");
        module.Setup<string?>("currentLocation", _ => true).SetResult(browserLocation);
        var rendered = RenderLayout(context);
        await SwitchWorkspaceAsync(rendered);
        Assert.AreEqual("#yaml", resolver.Request!.Location.Fragment);
        rendered.WaitForAssertion(() => Assert.AreEqual(browserLocation,
            rendered.Find("[data-testid=context-return-previous]").GetAttribute("href")));
    }

    [TestMethod]
    public async Task ANewBrowserAnchorCancelsAnOlderContextResolution()
    {
        var completion = new TaskCompletionSource<ConsoleContextSwitchDestination>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new SwitchResolver(_ => completion.Task);
        using var context = SwitchContext(resolver);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/agents/same?context=%2Finstance#overview");
        var source = navigation.Uri;
        var module = context.JSInterop.SetupModule("./_content/Agentstration.Web.Components/context-selector.js");
        var location = module.Setup<string?>("currentLocation", _ => true);
        location.SetResult(source.Replace("#overview", "#yaml", StringComparison.Ordinal));
        var rendered = RenderLayout(context);
        var pending = SwitchWorkspaceAsync(rendered);
        rendered.WaitForAssertion(() => Assert.IsNotNull(resolver.Request));
        location.SetResult(source.Replace("#overview", "#access", StringComparison.Ordinal));
        completion.SetResult(new("/agents?context=" + Uri.EscapeDataString(SwitchTarget)));
        await pending;
        Assert.AreEqual(source, navigation.Uri);
        Assert.AreEqual("/instance", context.Services.GetRequiredService<ConsoleContextState>().Current?.SelectedScopeRef);
    }

    [TestMethod]
    public async Task SelectorKeepsTheCollectionAndDoesNotUseNativeNavigation()
    {
        using var context = SwitchContext();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/agents?context=%2Finstance");
        var rendered = RenderLayout(context);
        await SwitchWorkspaceAsync(rendered);
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("/agents", new Uri(navigation.Uri).AbsolutePath);
            Assert.AreEqual(SwitchTarget, Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(navigation.Uri).Query)["context"].ToString());
            Assert.Contains("Current page", rendered.Markup);
        });
        Assert.IsFalse(context.JSInterop.Invocations.Any(call => call.Identifier == "selectContext"));
    }

    [TestMethod]
    public async Task ContextSwitchRefreshesShellStatusAfterLoadingTheTargetContext()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = SwitchContext();
        context.Services.AddScoped<IPlatformStatusProvider, SwitchingPlatformStatusProvider>();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/?context=%2Finstance");
        var rendered = RenderLayout(context);
        rendered.WaitForAssertion(() => Assert.AreEqual("Operational", rendered.Find(".health-indicator small").TextContent));
        await SwitchWorkspaceAsync(rendered);
        rendered.WaitForAssertion(() => Assert.AreEqual("Attention required", rendered.Find(".health-indicator small").TextContent));
        var provider = (SwitchingPlatformStatusProvider)context.Services.GetRequiredService<IPlatformStatusProvider>();
        CollectionAssert.AreEqual(new[] { "/instance", SwitchTarget }, provider.Contexts.ToArray());
    }

    private sealed class SwitchingPlatformStatusProvider(ConsoleContextState state) : IPlatformStatusProvider
    {
        public List<string> Contexts { get; } = [];
        public Task<PlatformStatusResult> GetStatusAsync(CancellationToken cancellationToken)
        {
            Contexts.Add(state.Current?.SelectedScopeRef ?? "unresolved");
            return Task.FromResult(state.Current?.SelectedKind == ConsoleContextKind.Instance
                ? new PlatformStatusResult(Models.UiStatus.Success, PlatformStatusKind.Operational)
                : new PlatformStatusResult(Models.UiStatus.Warning, PlatformStatusKind.AttentionRequired));
        }
    }

    [TestMethod]
    [DataRow("en-US", "Resource unavailable in this context")]
    [DataRow("fr-FR", "Ressource indisponible dans ce périmètre")]
    public async Task UnavailableResourceNoticeAppearsOnlyAfterAcceptedNavigationAndReturnsToTheSource(string locale, string title)
    {
        using var culture = new TestCultureScope(locale);
        var resolver = new SwitchResolver(request => Task.FromResult(new ConsoleContextSwitchDestination(request.FallbackLocation, true)));
        using var context = SwitchContext(resolver);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/agents/same?scopeRef=%2Finstance&context=%2Finstance#yaml");
        var source = navigation.Uri;
        var rendered = RenderLayout(context);
        await SwitchWorkspaceAsync(rendered);
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid=context-resource-unavailable]")));
        Assert.AreEqual(title, rendered.Find("[data-testid=context-resource-unavailable] h2").TextContent);
        Assert.Contains("button-secondary", rendered.Find("[data-testid=context-return-previous]").ClassName!);
        Assert.AreEqual(source, rendered.Find("[data-testid=context-return-previous]").GetAttribute("href"));
        Assert.AreEqual("/agents", new Uri(navigation.Uri).AbsolutePath);
        Assert.AreEqual(string.Empty, new Uri(navigation.Uri).Fragment);
    }

    [TestMethod]
    public async Task HiddenDraftUsesTheExistingGuardAndCancellationKeepsTheCurrentContext()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = SwitchContext();
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/agents/same?context=%2Finstance#yaml");
        var source = navigation.Uri;
        var rendered = context.Render<MainLayout>(parameters => parameters.Add(component => component.Body, DraftBody));
        await SwitchWorkspaceAsync(rendered);
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[role=alertdialog]")));
        Assert.AreEqual(source, navigation.Uri);
        Assert.IsEmpty(rendered.FindAll("[data-testid=context-resource-unavailable]"));
        await rendered.Find("[role=alertdialog] .button-secondary").ClickAsync(new());
        Assert.AreEqual(source, navigation.Uri);
        Assert.AreEqual("/instance", context.Services.GetRequiredService<ConsoleContextState>().Current?.SelectedScopeRef);
        Assert.IsEmpty(rendered.FindAll("[role=alertdialog]"));

        await SwitchWorkspaceAsync(rendered);
        await rendered.Find("[role=alertdialog] .button-danger").ClickAsync(new());
        rendered.WaitForAssertion(() => Assert.AreEqual(SwitchTarget, context.Services.GetRequiredService<ConsoleContextState>().Current?.SelectedScopeRef));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ResourceContentIsUnmountedWhileTheNewContextLoads(bool failTargetLoad)
    {
        using var context = SwitchContext();
        var provider = new DelayedNavigationProvider(failTargetLoad);
        context.Services.AddScoped<IConsoleContextProvider>(_ => provider);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/agents?context=%2Finstance");
        var rendered = RenderLayout(context);
        await SwitchWorkspaceAsync(rendered);
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(2, provider.CallCount);
            Assert.DoesNotContain("Current page", rendered.Markup);
            Assert.IsTrue(rendered.Find("[data-testid=context-selector-trigger]").HasAttribute("disabled"));
        });
        provider.CompleteWorkspace();
        if (failTargetLoad)
        {
            rendered.WaitForAssertion(() => Assert.IsNotNull(context.Services.GetRequiredService<ConsoleContextState>().Error));
            Assert.DoesNotContain("Current page", rendered.Markup);
            await rendered.InvokeAsync(() => navigation.NavigateTo("/agents?context=%2Finstance"));
        }
        rendered.WaitForAssertion(() => Assert.Contains("Current page", rendered.Markup));
        Assert.IsNull(context.Services.GetRequiredService<ConsoleContextState>().Error);
    }

    [TestMethod]
    public async Task AResolutionFailureKeepsTheSourcePageAndContext()
    {
        var resolver = new SwitchResolver(_ => Task.FromException<ConsoleContextSwitchDestination>(new HttpRequestException("Unavailable")));
        using var context = SwitchContext(resolver);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/agents/same?context=%2Finstance#yaml");
        var source = navigation.Uri;
        var rendered = RenderLayout(context);
        await SwitchWorkspaceAsync(rendered);
        Assert.AreEqual(source, navigation.Uri);
        Assert.AreEqual("/instance", context.Services.GetRequiredService<ConsoleContextState>().Current?.SelectedScopeRef);
        Assert.HasCount(1, context.Services.GetRequiredService<NotificationState>().Items);
    }

    [TestMethod]
    public async Task LateResolutionCannotNavigateAwayFromANewerPage()
    {
        var completion = new TaskCompletionSource<ConsoleContextSwitchDestination>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new SwitchResolver(_ => completion.Task);
        using var context = SwitchContext(resolver);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/agents/same?context=%2Finstance#yaml");
        var rendered = RenderLayout(context);
        var pending = SwitchWorkspaceAsync(rendered);
        rendered.WaitForAssertion(() => Assert.IsNotNull(resolver.Request));
        await rendered.InvokeAsync(() => navigation.NavigateTo("/flows?context=%2Finstance"));
        completion.SetResult(new("/agents?context=" + Uri.EscapeDataString(SwitchTarget)));
        await pending;
        Assert.AreEqual("/flows", new Uri(navigation.Uri).AbsolutePath);
        Assert.AreEqual("/instance", Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(navigation.Uri).Query)["context"].ToString());
    }

    private static RenderFragment DraftBody => builder =>
    {
        builder.OpenComponent<UnsavedNavigationGuard>(0);
        builder.AddAttribute(1, nameof(UnsavedNavigationGuard.IsDirty), true);
        builder.CloseComponent();
        builder.AddMarkupContent(2, "<p>Hidden draft without a visible draft bar</p>");
    };

    private static BunitContext SwitchContext(SwitchResolver? resolver = null)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddAgentstrationWebComponents();
        context.Services.AddScoped<IConsoleContextProvider>(_ => new SwitchingContextProvider(AllNavigationPermissions));
        context.Services.AddSingleton<IConsoleContextSwitchResolver>(resolver ?? new SwitchResolver(request => Task.FromResult(new ConsoleContextSwitchDestination(request.FallbackLocation))));
        return context;
    }

    private static async Task SwitchWorkspaceAsync(IRenderedComponent<MainLayout> rendered)
    {
        await rendered.WaitForElement("[data-testid=context-selector-trigger]").ClickAsync(new());
        await rendered.Find("[data-testid=context-option-workspace]").ClickAsync(new());
    }

    private sealed class SwitchResolver(Func<ConsoleContextSwitchRequest, Task<ConsoleContextSwitchDestination>> resolve) : IConsoleContextSwitchResolver
    {
        public ConsoleContextSwitchRequest? Request { get; private set; }
        public Task<ConsoleContextSwitchDestination> ResolveAsync(ConsoleContextSwitchRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return resolve(request);
        }
    }
}
