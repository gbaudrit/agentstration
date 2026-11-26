using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Sources.Contracts;
using Agentstration.Tools;
using Agentstration.Web.Components;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
namespace Agentstration.Web.Tests;

public sealed partial class ExtensionsConsoleTests
{
    [TestMethod]
    [DataRow("en-US")]
    [DataRow("fr-FR")]
    public async Task SourceProviderInstancePreviewDoesNotReadUntilSelected(string locale)
    {
        using var culture = new TestCultureScope(locale);
        using var context = new BunitContext().WithCollectionPresentation().WithResourceListContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IExtensionsClient>(new FakeExtensionsClient(configured: false, source: true));
        context.Services.AddSingleton<IModelProfilesClient>(new FakeModelProfilesClient());
        var sources = new ExactSourceProvidersClient();
        context.Services.AddSingleton<ISourceProvidersClient>(sources);
        context.Services.AddSingleton(new NotificationState());
        await context.Services.GetRequiredService<ConsoleContextState>().EnsureLoadedAsync(default);
        var cut = context.Render<Agentstration.Web.Components.Pages.Extensions>();
        cut.WaitForElement("[data-testid=extension-source-provider-preview]");
        Assert.AreEqual(0, sources.Reads);
        Assert.IsEmpty(cut.FindComponents<SourceProviderResourcePreview>());
        await cut.Find("[data-testid=extension-source-provider-preview]").ClickAsync(new());
        Assert.AreEqual(1, sources.Reads);
        Assert.AreEqual(ScopedResourceAddress.Create(ResourceScopeRef.Instance, ResourceNamespace.Default, SourceResourceKinds.SourceProvider, "saved-git"), sources.Requested);
        Assert.HasCount(1, cut.FindComponents<SourceProviderResourceSummary>());
        Assert.AreEqual(1, sources.CatalogReads);
    }
    private sealed class ExactSourceProvidersClient : ISourceProvidersClient
    {
        public int Reads { get; private set; }
        public int CatalogReads { get; private set; }
        public ScopedResourceAddress Requested { get; private set; }
        public Task<IReadOnlyList<SourceProviderSummaryResponse>> GetSourceProvidersAsync(CancellationToken cancellationToken)
        {
            CatalogReads++;
            return Task.FromResult<IReadOnlyList<SourceProviderSummaryResponse>>([new("saved-git", "saved-git", "Saved Git", "llama-cpp-local", "default", "git", "available", null, "default", ResourceScopeRef.Instance, ResourceScopeRef.Instance)]);
        }
        public Task<ResourceSnapshot<SourceProviderResource>> GetSourceProviderAsync(ResourceScopeRef owner, ResourceNamespace ns, string name, CancellationToken cancellationToken)
        {
            Reads++; Requested = ScopedResourceAddress.Create(owner, ns, SourceResourceKinds.SourceProvider, name);
            return Task.FromResult(new ResourceSnapshot<SourceProviderResource>(new()
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = SourceResourceKinds.SourceProvider,
                Metadata = new() { Name = name, Namespace = ns },
                ScopeRef = owner,
                Uid = Guid.NewGuid(),
                Definition = new() { DisplayName = "Saved Git", Extension = new("llama-cpp-local", owner, ns), ContributionId = "git" }
            }, "\"source-etag\""));
        }
        public Task<ResourceSnapshot<SourceProviderResource>> GetSourceProviderAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SourceProviderResource>> CreateSourceProviderAsync(CreateSourceProviderRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<SourceProviderResource>> UpdateSourceProviderAsync(ResourceNamespace @namespace, string name, PutSourceProviderRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteSourceProviderAsync(ResourceNamespace @namespace, string name, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SourceProviderStatusResponse> GetStatusAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SourceProviderUsagesResponse> GetUsagesAsync(ResourceNamespace @namespace, string name, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
