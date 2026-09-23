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
            Assert.IsTrue(rendered.Find(".health-pulse").ClassList.Contains("health-success"));
        });
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

        public Task<PlatformStatusResult> GetStatusAsync(CancellationToken cancellationToken) =>
            completion.Task.WaitAsync(cancellationToken);

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
