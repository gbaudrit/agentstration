using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ToolCatalogTests
{
    [TestMethod]
    public void CatalogLoadsToolsAcrossAllProviders()
    {
        using var context = new BunitContext();
        var client = new CatalogClient();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IToolsClient>(client);

        var rendered = context.Render<Agentstration.Web.Components.Pages.Tools>();

        rendered.WaitForAssertion(() =>
        {
            Assert.IsTrue(client.WasRequested);
            Assert.IsNull(client.RequestedProvider);
        });
    }

    private sealed class CatalogClient : IToolsClient
    {
        public bool WasRequested { get; private set; }
        public string? RequestedProvider { get; private set; }

        public Task<IReadOnlyList<ToolResource>> GetToolsAsync(string? provider = null, CancellationToken cancellationToken = default)
        {
            WasRequested = true;
            RequestedProvider = provider;
            return Task.FromResult<IReadOnlyList<ToolResource>>([]);
        }

        public Task<IReadOnlyList<ToolProviderResource>> GetProvidersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolProviderResource>> GetProviderAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolProviderResource>> CreateProviderAsync(CreateToolProviderRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolProviderResource>> UpdateProviderAsync(string name, PutToolProviderRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ToolConnectionTestResponse> TestAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ToolDiscoveryDiffResponse> RefreshAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolResource>> GetToolAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolResource>> SetEnabledAsync(string name, bool enabled, string? etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
