using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Resources;
using Agentstration.Schemas;
using Agentstration.Schemas.Contracts;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class OperationSchemaCandidateTests
{
    [TestMethod]
    [DataRow("en-US", "Create Schema draft")]
    [DataRow("fr-FR", "Créer un brouillon Schema")]
    public async Task CandidateInspectionDoesNotPublishOrAdoptAndDraftCreationRetainsSourceEvidence(string locale, string action)
    {
        using var culture = new TestCultureScope(locale);
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        using var handler = new DraftHandler();
        using var http = new HttpClient(handler) { BaseAddress = new("https://agentstration.test/") };
        context.Services.AddSingleton<ISchemasClient>(new SchemasApiClient(http));
        var operation = new ConnectedServiceOperationResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = ConnectedServiceResourceKinds.ConnectedServiceOperation,
            Metadata = new() { Name = "items", Namespace = ResourceNamespace.Default },
            ScopeRef = ResourceScopeRef.Instance, Uid = Guid.NewGuid(), Generation = 3,
            Definition = new()
            {
                DisplayName = "Items · 200 application/json", ExternalId = "items-200", DefinitionDigest = "operation-digest",
                Service = null!, Type = null!, Provenance = null!,
                Definition = JsonSerializer.Deserialize<JsonElement>("""
                    {"outputSchema":{"type":"object","properties":{"results":{"type":"array","items":{"type":"object"}}}},
                     "responseShape":{"x-agentstration-response":{"status":"200","mediaType":"application/json","contractDigest":"source-digest","nativeDefinition":{"type":"object"}}}}
                    """)
            }
        };
        var component = context.Render<OperationSchemaCandidate>(p => p.Add(c => c.Operation, operation).Add(c => c.CanManage, true));
        Assert.AreEqual(0, handler.Calls);
        Assert.Contains(action, component.Markup);
        component.Find("input").Change("items-response");
        await component.Find("[data-testid=operation-schema-create]").ClickAsync(new());
        Assert.AreEqual(1, handler.Calls);
        Assert.AreEqual(ResourceScopeRef.Instance, handler.Request!.ScopeRef);
        Assert.AreEqual("source-digest", handler.Request.Properties.SourceEvidence!.Value.GetProperty("Candidate").GetProperty("contractDigest").GetString());
        Assert.IsTrue(handler.Request.Properties.Content.GetProperty("properties").TryGetProperty("results", out _));
        Assert.Contains("/schemas/items-response", context.Services.GetRequiredService<NavigationManager>().Uri);
    }

    private sealed class DraftHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public CreateSchemaRequest? Request { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/schemas", request.RequestUri!.AbsolutePath);
            Request = await request.Content!.ReadFromJsonAsync<CreateSchemaRequest>(cancellationToken);
            var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(new SchemaResource
            {
                ApiVersion = ResourceApiVersions.CoreV1, Kind = SchemaResourceKinds.Schema,
                ScopeRef = Request!.ScopeRef, Metadata = new() { Name = Request.Name }, Definition = Request.Properties
            }) };
            response.Headers.ETag = new("\"draft\"");
            return response;
        }
    }
}
