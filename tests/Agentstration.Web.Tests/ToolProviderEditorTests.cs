using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ToolProviderEditorTests
{
    [TestMethod]
    public void SectionLinksRetainTheProviderRouteBeforeApplyingTheFragment()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<IToolsClient>(new ProviderClient());

        var rendered = context.Render<ToolProviderEditor>(parameters => parameters
            .Add(value => value.Name, "microsoft-learn"));

        rendered.WaitForAssertion(() =>
        {
            var links = rendered.FindAll("nav.resource-tabs a");
            Assert.HasCount(3, links);
            CollectionAssert.AreEqual(
                new[]
                {
                    "/tools/providers/microsoft-learn#overview",
                    "/tools/providers/microsoft-learn#tools",
                    "/tools/providers/microsoft-learn#connection"
                },
                links.Select(link => link.GetAttribute("href")).ToArray());
        });
    }

    private sealed class ProviderClient : IToolsClient
    {
        private static readonly ToolProviderResource Provider = new()
        {
            ApiVersion = ManagementApiVersions.CoreV1,
            Kind = ResourceKinds.ToolProvider,
            Metadata = new ResourceMetadata { Name = "microsoft-learn" },
            Generation = 1,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded },
            Definition = new ToolProviderProperties
            {
                DisplayName = "Microsoft Learn",
                ProviderType = ToolProviderType.Mcp,
                Mcp = new McpToolProviderConfiguration
                {
                    Transport = McpToolProviderTransport.StreamableHttp,
                    Endpoint = new Uri("https://learn.microsoft.com/api/mcp")
                }
            }
        };

        public Task<ResourceSnapshot<ToolProviderResource>> GetProviderAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(new ResourceSnapshot<ToolProviderResource>(Provider, "etag"));
        public Task<IReadOnlyList<ToolResource>> GetToolsAsync(string? provider = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ToolResource>>([]);
        public Task<IReadOnlyList<ToolProviderResource>> GetProvidersAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolProviderResource>> CreateProviderAsync(CreateToolProviderRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolProviderResource>> UpdateProviderAsync(string name, PutToolProviderRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ToolConnectionTestResponse> TestAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ToolDiscoveryDiffResponse> RefreshAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolResource>> GetToolAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ToolResource>> SetEnabledAsync(string name, bool enabled, string? etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
