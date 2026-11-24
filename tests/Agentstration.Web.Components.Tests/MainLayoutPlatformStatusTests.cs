using System.Globalization;
using Agentstration.Web.Components.Models;
using Agentstration.Web.Components.State;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Components.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MainLayoutPlatformStatusTests
{
    [TestMethod]
    public void DirectNonOverviewRenderingResolvesShellPlatformStatus()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext(out var provider);
        var rendered = RenderAgentsRoute(context);

        Assert.AreEqual("Connecting", rendered.Find(".health-indicator small").TextContent);

        provider.Complete(new(UiStatus.Success, PlatformStatusKind.Operational));

        rendered.WaitForAssertion(() =>
            Assert.AreEqual("Operational", rendered.Find(".health-indicator small").TextContent));
    }

    [TestMethod]
    public void ProviderFailureResolvesShellPlatformStatusAsUnavailable()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext(out var provider);
        var rendered = RenderAgentsRoute(context);

        provider.Fail(new HttpRequestException("Authoritative API unavailable"));

        rendered.WaitForAssertion(() =>
            Assert.AreEqual("Unavailable", rendered.Find(".health-indicator small").TextContent));
    }

    [TestMethod]
    public void PlatformStatusUsesFrenchShellLocalization()
    {
        using var culture = new CultureScope("fr-FR");
        using var context = CreateContext(out var provider);
        var rendered = RenderAgentsRoute(context);

        provider.Complete(new(UiStatus.Success, PlatformStatusKind.Operational));

        rendered.WaitForAssertion(() =>
            Assert.AreEqual("Opérationnelle", rendered.Find(".health-indicator small").TextContent));
    }

    [TestMethod]
    public void NoActiveDeploymentsRendersAsHealthy()
    {
        using var culture = new CultureScope("fr-FR");
        using var context = CreateContext(out var provider);
        var rendered = RenderAgentsRoute(context);

        provider.Complete(new(UiStatus.Success, PlatformStatusKind.NoActiveDeployments));

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("Aucun déploiement actif", rendered.Find(".health-indicator small").TextContent);
            Assert.IsTrue(rendered.Find(".health-indicator .status-badge").ClassList.Contains("status-success"));
        });
    }

    [TestMethod]
    public void FeatureStatusReusesTheConsoleFrameWithoutLoadingOperationalSources()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext(out var provider);
        var rendered = context.Render<MainLayout>(parameters => parameters
            .Add(x => x.SectionTitle, "Feature preparation")
            .Add(x => x.EnableCommandPalette, false)
            .Add(x => x.StatusContent, builder => builder.AddContent(0, "Feature status"))
            .Add(x => x.Body, builder => builder.AddContent(0, "Progress body")));
        Assert.AreEqual(0, provider.Calls);
        Assert.IsTrue(rendered.Find("[data-testid='console-command-trigger']").HasAttribute("disabled"));
        Assert.AreEqual(1, rendered.FindAll("[data-testid='console-sidebar']").Count);
        StringAssert.Contains(rendered.Find("[data-testid='console-breadcrumb']").TextContent, "Feature preparation");
        StringAssert.Contains(rendered.Find(".sidebar-footer").TextContent, "Feature status");
        StringAssert.Contains(rendered.Find("main").TextContent, "Progress body");
    }

    private static BunitContext CreateContext(out ControlledPlatformStatusProvider provider)
    {
        var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddAgentstrationWebComponents();
        var controlledProvider = new ControlledPlatformStatusProvider();
        provider = controlledProvider;
        context.Services.AddScoped<IPlatformStatusProvider>(_ => controlledProvider);
        return context;
    }

    private static IRenderedComponent<MainLayout> RenderAgentsRoute(BunitContext context) =>
        context.Render<MainLayout>(parameters => parameters.Add(
            component => component.Body,
            (RenderFragment)(builder => builder.AddMarkupContent(0, "<p>Agents route</p>"))));

    private sealed class ControlledPlatformStatusProvider : IPlatformStatusProvider
    {
        private readonly TaskCompletionSource<PlatformStatusResult> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls { get; private set; }
        public Task<PlatformStatusResult> GetStatusAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return completion.Task.WaitAsync(cancellationToken);
        }

        public void Complete(PlatformStatusResult result) => completion.SetResult(result);
        public void Fail(Exception exception) => completion.SetException(exception);
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo originalCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;

        public CultureScope(string cultureName)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
