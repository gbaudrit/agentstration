using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ConnectedServiceDiscoveryPanelTests
{
    [TestMethod]
    [DataRow("en-US", "Service operations", "Discovery accepted")]
    [DataRow("fr-FR", "Opérations du service", "Découverte acceptée")]
    public async Task DiscoveryBelongsToItsServiceAndExposesScopedRunHistoryAndExplicitActivation(string cultureName, string operationsLabel, string accepted)
    {
        using var culture = new TestCultureScope(cultureName);
        using var handler = new DiscoveryHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = await SetupAsync(http, true);
        var rendered = context.Render<ConnectedServiceDiscoveryPanel>(p => p.Add(c => c.Service, handler.Service).Add(c => c.Supported, true));
        rendered.WaitForAssertion(() =>
        {
            Assert.Contains(operationsLabel, rendered.Markup);
            Assert.Contains(cultureName == "fr-FR" ? "Les valeurs d’exemple sont écartées" : "Example values are discarded", rendered.Find("section").TextContent);
            Assert.HasCount(1, rendered.FindAll("[data-testid='connected-service-operation-row']"));
            Assert.IsFalse(rendered.Find("[data-testid='connected-service-start-discovery']").HasAttribute("disabled"));
        });
        Assert.AreEqual(0, handler.Discoveries);
        Assert.AreEqual(0, handler.Activations);
        await rendered.Find("[data-testid='connected-service-operation-enabled']").ClickAsync(new());
        Assert.AreEqual(1, handler.Activations);
        Assert.AreEqual("\"reviewed\"", handler.ActivationEtag);
        Assert.IsTrue(handler.Operation.Enabled);
        await rendered.Find("[data-testid='connected-service-start-discovery']").ClickAsync(new());
        rendered.WaitForAssertion(() => Assert.Contains(accepted, rendered.Markup));
        Assert.AreEqual(1, handler.Discoveries);
        Assert.IsTrue(rendered.Find("[data-testid='connected-service-start-discovery']").HasAttribute("disabled"));
        var link = new Uri("https://agentstration.test" + rendered.Find("[data-testid='connected-service-discovery-flow-link']").GetAttribute("href"));
        var query = System.Web.HttpUtility.ParseQueryString(link.Query);
        Assert.AreEqual("/flow-runs/flowrun-fixture", link.AbsolutePath);
        Assert.AreEqual(ResourceScopeRef.Workspace(handler.WorkspaceId).Value, query["scopeRef"]);
        Assert.AreEqual("/instance", query["context"]);
        await rendered.Find("[data-testid='connected-service-refresh-discovery']").ClickAsync(new());
        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("Succeeded", rendered.Find("[data-testid='connected-service-discovery-row']").GetAttribute("data-state"));
            Assert.IsFalse(rendered.Find("[data-testid='connected-service-start-discovery']").HasAttribute("disabled"));
        });
    }

    [TestMethod]
    public async Task ReadOnlyServiceShowsOperationsAndHistoryWithoutMutationControls()
    {
        using var handler = new DiscoveryHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        using var context = await SetupAsync(http, false);
        var rendered = context.Render<ConnectedServiceDiscoveryPanel>(p => p.Add(c => c.Service, handler.Service).Add(c => c.Supported, true));
        rendered.WaitForElement("[data-testid='connected-service-operation-row']");
        Assert.IsEmpty(rendered.FindAll("[data-testid='connected-service-start-discovery'], [data-testid='connected-service-operation-enabled']"));
        Assert.AreEqual(0, handler.Discoveries);
        Assert.AreEqual(0, handler.Activations);
    }

    private static async Task<BunitContext> SetupAsync(HttpClient http, bool write)
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var state = new ConsoleContextState(new ContextProvider(write));
        await state.EnsureLoadedAsync(default);
        context.Services.AddSingleton(state);
        context.Services.AddSingleton<IConnectedServiceOperationsClient>(new ConnectedServiceOperationsApiClient(http));
        return context;
    }

    private sealed class DiscoveryHandler : HttpMessageHandler
    {
        public Guid WorkspaceId { get; } = Guid.NewGuid();
        public int Discoveries { get; private set; }
        public int Activations { get; private set; }
        public string? ActivationEtag { get; private set; }
        private ConnectedServiceOperationDiscoveryResource? discovery;
        public ConnectedServiceResource Service { get; } = new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = "ConnectedService",
            Uid = Guid.NewGuid(),
            Generation = 3,
            ScopeRef = ResourceScopeRef.Instance,
            Metadata = new() { Name = "fixture" },
            Definition = new() { DisplayName = "Fixture", Type = new()
            {
                Type = new("http-endpoint", ResourceScopeRef.Instance), TypeUid = Guid.NewGuid(), Version = "1.0.0", SchemaDigest = "digest"
            }, Options = JsonSerializer.SerializeToElement(new { }) }
        };
        public ConnectedServiceOperationResource Operation { get; private set; }
        public DiscoveryHandler()
        {
            Operation = new()
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = "ConnectedServiceOperation",
                Uid = Guid.NewGuid(),
                Generation = 1,
                ScopeRef = ResourceScopeRef.Instance,
                Metadata = new() { Name = "fixture-operation" },
                Definition = new()
                {
                    DisplayName = "Inventory",
                    ExternalId = "fixture.read",
                    DefinitionDigest = "digest",
                    Type = null!,
                    Provenance = null!,
                    Definition = JsonSerializer.SerializeToElement(new { }),
                    Service = new() { ServiceUid = Service.Uid, Generation = Service.Generation, Service = new(Service.Address, Service.ScopeRef!.Value) }
                },
                ObservedState = new() { Availability = ConnectedServiceOperationAvailability.Available, ObservedAt = DateTimeOffset.UtcNow }
            };
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            object body;
            if (path == "/api/connected-services/fixture/operation-discoveries")
            {
                Assert.AreEqual("/instance", System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["scopeRef"]);
                if (request.Method == HttpMethod.Post)
                {
                    Discoveries++;
                    discovery = new()
                    {
                        ApiVersion = ResourceApiVersions.CoreV1,
                        Kind = "ConnectedServiceOperationDiscovery",
                        Uid = Guid.NewGuid(),
                        ScopeRef = ResourceScopeRef.Workspace(WorkspaceId),
                        LogicalScopeRef = ResourceScopeRef.Instance,
                        Metadata = new() { Name = "discovery-fixture" },
                        Service = Operation.Definition.Service,
                        ServiceType = null!,
                        Flow = null!,
                        FlowRunId = "flowrun-fixture",
                        State = ConnectedServiceOperationDiscoveryState.Pending,
                        CorrelationId = "test",
                        RequestHash = "hash",
                        CreatedBy = Guid.NewGuid(),
                        TenantId = Guid.NewGuid(),
                        WorkspaceId = WorkspaceId,
                        CreatedAt = DateTimeOffset.UtcNow
                    };
                    body = discovery;
                }
                else
                {
                    if (discovery is not null) discovery = discovery with { State = ConnectedServiceOperationDiscoveryState.Succeeded };
                    body = discovery is null ? Array.Empty<ConnectedServiceOperationDiscoveryResource>() : new[] { discovery };
                }
            }
            else if (path == "/api/connected-services/fixture/operations")
                body = new[] { Operation, Operation with { Uid = Guid.NewGuid(), Definition = Operation.Definition with { Service = Operation.Definition.Service with { ServiceUid = Guid.NewGuid() } } } };
            else if (path == "/api/connected-service-operations/fixture-operation") body = Operation;
            else if (path == "/api/connected-service-operations/fixture-operation/enabled")
            {
                ActivationEtag = request.Headers.IfMatch.Single().Tag;
                var change = await request.Content!.ReadFromJsonAsync<SetConnectedServiceOperationEnabledRequest>(cancellationToken);
                Activations++;
                Operation = Operation with { Enabled = change!.Enabled, Generation = Operation.Generation + 1 };
                body = Operation;
            }
            else return new(HttpStatusCode.NotFound);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
            response.Headers.ETag = new("\"reviewed\"");
            return response;
        }
    }
    private sealed class ContextProvider(bool write) : IConsoleContextProvider
    {
        public Task<ConsoleContextSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ConsoleContextSnapshot(
            Guid.NewGuid(), "Administrator", "/instance", ConsoleContextKind.Instance, Guid.NewGuid(), "instance", "Instance",
            Guid.NewGuid(), "operations", "Operations", new HashSet<string>(write ? [AuthorizationPermissions.ResourcesWrite] : [AuthorizationPermissions.ResourcesRead]), [], []));
    }
}
