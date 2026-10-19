using Agentstration.Web.Components;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Console.Components.Tests;

[TestClass]
public sealed class CommandPaletteFallbackTests
{
    [TestMethod]
    public async Task PaletteUsesSemanticIconsForSearchDestinationsAndCommands()
    {
        using var context = CreateContext([], []);
        var rendered = context.Render<MainLayout>(parameters => parameters.Add(value => value.Body, (RenderFragment)(_ => { })));

        await rendered.Find("[data-testid='console-command-trigger']").ClickAsync(new());

        Assert.IsNotNull(rendered.Find(".command-search-box .ui-icon"));
        Assert.AreEqual("home", rendered.WaitForElement(".command-result .command-icon").GetAttribute("data-icon"));

        await rendered.Find("[data-testid='console-command-input']").InputAsync(new ChangeEventArgs { Value = "nouveau agent" });

        rendered.WaitForAssertion(() =>
            Assert.AreEqual("plus", rendered.Find(".command-result .command-icon").GetAttribute("data-icon")));
    }

    [TestMethod]
    public async Task ExistingPageMatchTakesPriorityOverEntryFallback()
    {
        using var context = CreateContext([], [new("Assistant", "/entry-interactions/fallback")]);
        var fallback = context.Services.GetRequiredService<StubFallbackProvider>();
        var rendered = context.Render<MainLayout>(parameters => parameters.Add(value => value.Body, (RenderFragment)(_ => { })));

        await OpenAndSearchAsync(rendered, "Agents");

        rendered.WaitForAssertion(() => Assert.IsTrue(rendered.Markup.Contains("Agents", StringComparison.Ordinal)));
        Assert.AreEqual(0, fallback.Calls);
        Assert.AreEqual("http://localhost/", context.Services.GetRequiredService<NavigationManager>().Uri);
    }

    [TestMethod]
    public async Task ExistingResourceMatchTakesPriorityOverEntryFallback()
    {
        var resource = new ResourceSearchResult("Ollama", "Model provider", "default/models/ollama", "/modelproviders/ollama", "Ready", "cpu");
        using var context = CreateContext([resource], [new("Assistant", "/entry-interactions/fallback")]);
        var fallback = context.Services.GetRequiredService<StubFallbackProvider>();
        var rendered = context.Render<MainLayout>(parameters => parameters.Add(value => value.Body, (RenderFragment)(_ => { })));

        await OpenAndSearchAsync(rendered, "ollama");

        rendered.WaitForAssertion(() => Assert.IsTrue(rendered.Markup.Contains("Model provider", StringComparison.Ordinal)));
        Assert.AreEqual(0, fallback.Calls);
    }

    [TestMethod]
    public async Task UnmatchedQueryOffersFallbackWithoutExecutingAndKeyboardEnterNavigates()
    {
        const string target = "/entry-interactions/33333333-3333-3333-3333-333333333333/default/assistant?query=exact%20query";
        using var context = CreateContext([], [new("Assistant", target)]);
        var rendered = context.Render<MainLayout>(parameters => parameters.Add(value => value.Body, (RenderFragment)(_ => { })));

        await OpenAndSearchAsync(rendered, "exact query");

        rendered.WaitForElement("[data-testid='console-entry-fallback']", TimeSpan.FromSeconds(3));
        Assert.AreEqual("http://localhost/", context.Services.GetRequiredService<NavigationManager>().Uri, "Typing must not execute or navigate.");
        await rendered.Find("[data-testid='console-command-input']").KeyDownAsync(new KeyboardEventArgs { Key = "Enter" });
        Assert.AreEqual($"http://localhost{target}", context.Services.GetRequiredService<NavigationManager>().Uri);
    }

    [TestMethod]
    public async Task UnmatchedQueryOffersEveryFallbackAndSelectedEntryNavigates()
    {
        const string firstTarget = "/entry-interactions/33333333-3333-3333-3333-333333333333/default/first?query=exact%20query";
        const string secondTarget = "/entry-interactions/33333333-3333-3333-3333-333333333333/default/second?query=exact%20query";
        using var context = CreateContext([], [new("First", firstTarget), new("Second", secondTarget)]);
        var rendered = context.Render<MainLayout>(parameters => parameters.Add(value => value.Body, (RenderFragment)(_ => { })));

        await OpenAndSearchAsync(rendered, "exact query");

        var fallbacks = rendered.WaitForElements("[data-testid='console-entry-fallback']", 2, TimeSpan.FromSeconds(3));
        StringAssert.Contains(fallbacks[0].TextContent, "First");
        StringAssert.Contains(fallbacks[1].TextContent, "Second");
        await fallbacks[1].ClickAsync(new());
        Assert.AreEqual($"http://localhost{secondTarget}", context.Services.GetRequiredService<NavigationManager>().Uri);
    }

    [TestMethod]
    public async Task UnmatchedQueryWithoutFallbackKeepsTheEmptyState()
    {
        using var context = CreateContext([], []);
        var rendered = context.Render<MainLayout>(parameters => parameters.Add(value => value.Body, (RenderFragment)(_ => { })));

        await OpenAndSearchAsync(rendered, "nothing matches this");

        rendered.WaitForElement(".command-empty", TimeSpan.FromSeconds(3));
    }

    private static BunitContext CreateContext(
        IReadOnlyList<ResourceSearchResult> resources,
        IReadOnlyList<CommandPaletteFallbackResult> fallbacks)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddSingleton<IResourceSearchProvider>(new StubResourceSearchProvider(resources));
        context.Services.AddSingleton(new StubFallbackProvider(fallbacks));
        context.Services.AddSingleton<ICommandPaletteFallbackProvider>(provider => provider.GetRequiredService<StubFallbackProvider>());
        context.Services.AddAgentstrationWebComponents();
        return context;
    }

    private static async Task OpenAndSearchAsync(IRenderedComponent<MainLayout> rendered, string query)
    {
        await rendered.Find("[data-testid='console-command-trigger']").ClickAsync(new());
        await rendered.Find("[data-testid='console-command-input']").InputAsync(new ChangeEventArgs { Value = query });
    }

    private sealed class StubResourceSearchProvider(IReadOnlyList<ResourceSearchResult> results) : IResourceSearchProvider
    {
        public Task<IReadOnlyList<ResourceSearchResult>> SearchAsync(string query, CancellationToken cancellationToken) => Task.FromResult(results);
    }

    private sealed class StubFallbackProvider(IReadOnlyList<CommandPaletteFallbackResult> results) : ICommandPaletteFallbackProvider
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<CommandPaletteFallbackResult>> ResolveAsync(string query, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(results);
        }
    }
}
