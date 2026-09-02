namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ThemeStaticAssetTests
{
    [TestMethod]
    public async Task AuthenticationPageUsesTheLightLockup()
    {
        var layout = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "TestAssets", "_AuthenticationLayout.cshtml"));
        var css = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "TestAssets", "auth.css"));

        StringAssert.Contains(layout, "images/agentstration-lockup-light.png");
        Assert.IsFalse(layout.Contains("<span><strong>Agentstration</strong>", StringComparison.Ordinal));
        StringAssert.Contains(css, ".auth-brand img{display:block;width:218px;max-width:100%;height:auto}");
    }

    [TestMethod]
    public async Task ConsoleShellUsesTheDarkLockupOnItsDarkSidebar()
    {
        var layout = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "TestAssets", "MainLayout.razor"));
        var css = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "TestAssets", "design-tokens.css"));

        StringAssert.Contains(layout, "images/agentstration-lockup-dark.png");
        Assert.IsFalse(layout.Contains("images/agentstration-lockup-light.png", StringComparison.Ordinal));
        Assert.IsFalse(css.Contains("mix-blend-mode: screen", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ProgrammaticallyFocusedPageTitlesDoNotShowAVisualOutline()
    {
        var css = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "TestAssets", "design-tokens.css"));

        StringAssert.Contains(css, ".content h1:focus { outline: none; }");
    }

    [TestMethod]
    public async Task FormTokensResolveInsideTheSelectedTheme()
    {
        var css = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "TestAssets", "design-tokens.css"));

        var appShellStart = css.IndexOf(".app-shell {", StringComparison.Ordinal);
        var darkThemeStart = css.IndexOf(".app-shell.theme-dark {", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, appShellStart);
        Assert.IsGreaterThan(appShellStart, darkThemeStart);

        var appShellTokens = css[appShellStart..darkThemeStart];
        StringAssert.Contains(appShellTokens, "--field-background: var(--color-surface-muted);");
        StringAssert.Contains(appShellTokens, "--field-border: var(--color-border-strong);");
        StringAssert.Contains(appShellTokens, "color-scheme: light;");

        var darkThemeEnd = css.IndexOf("/* Shared form controls", darkThemeStart, StringComparison.Ordinal);
        Assert.IsGreaterThan(darkThemeStart, darkThemeEnd);
        StringAssert.Contains(css[darkThemeStart..darkThemeEnd], "color-scheme: dark;");
    }

    [TestMethod]
    public async Task SidebarAccommodatesLocalizedLabelsWithoutPersistentScrollbarChrome()
    {
        var css = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "TestAssets", "design-tokens.css"));

        StringAssert.Contains(css, "--sidebar-width: 280px;");
        StringAssert.Contains(css, ".side-nav { scrollbar-width: thin; scrollbar-color: transparent transparent; }");
        StringAssert.Contains(css, ".side-nav:hover,.side-nav:focus-within { scrollbar-color: #344861 transparent; }");
    }
}
