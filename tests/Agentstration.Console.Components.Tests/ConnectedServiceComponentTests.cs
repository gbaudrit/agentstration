using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

using ConnectedServicesPage = Agentstration.Web.Components.Pages.ConnectedServices;

[TestClass]
public sealed class ConnectedServiceComponentTests
{
    [TestMethod]
    public void CatalogRendersTypeAvailabilityAndExactScope()
    {
        using var context = new BunitContext();
        var scope = ResourceScopeRef.Workspace(Guid.NewGuid());
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConnectedServicesClient>(new StubClient(Resource(scope)));

        var rendered = context.Render<ConnectedServicesPage>();

        rendered.WaitForAssertion(() =>
        {
            var row = rendered.Find("[data-testid='connected-service-row']");
            StringAssert.Contains(row.TextContent, "Redis");
            StringAssert.Contains(row.TextContent, "Ready");
            StringAssert.Contains(row.InnerHtml, $"scopeRef={Uri.EscapeDataString(scope.Value)}");
        });
    }

    private static ConnectedServiceResource Resource(ResourceScopeRef scope) => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1,
        Kind = ConnectedServiceResourceKinds.ConnectedService,
        Metadata = new() { Name = "redis" },
        ScopeRef = scope,
        Definition = new()
        {
            DisplayName = "Redis",
            Type = new() { Type = new ResourceReference("redis", ResourceScopeRef.Instance, new("azure")), TypeUid = Guid.NewGuid(), Version = "1.0.0", SchemaDigest = "sha256:contract" },
            Options = JsonSerializer.SerializeToElement(new { })
        },
        ObservedState = new() { Availability = ConnectedServiceAvailability.Ready, ObservedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) }
    };

    private sealed class StubClient(ConnectedServiceResource resource) : IConnectedServicesClient
    {
        public Task<IReadOnlyList<ConnectedServiceTypeResource>> GetTypesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<ConnectedServiceResource>> GetConnectedServicesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ConnectedServiceResource>>([resource]);
        public Task<IReadOnlyList<ResourceScopeTargetResponse>> GetScopeTargetsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> GetConnectedServiceAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ConnectedServiceUsagesResponse> GetUsagesAsync(string name, ResourceScopeRef scopeRef, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> CreateConnectedServiceAsync(CreateConnectedServiceRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> UpdateConnectedServiceAsync(string name, ResourceScopeRef scopeRef, PutConnectedServiceRequest request, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> ReconcileAsync(string name, ResourceScopeRef scopeRef, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ResourceSnapshot<ConnectedServiceResource>> DeactivateAsync(string name, ResourceScopeRef scopeRef, string etag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
