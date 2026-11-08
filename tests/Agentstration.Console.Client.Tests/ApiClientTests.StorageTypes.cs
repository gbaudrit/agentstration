using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

public sealed partial class ApiClientTests
{
    [TestMethod]
    [DataRow("get")]
    [DataRow("update")]
    [DataRow("publish")]
    [DataRow("delete")]
    public async Task StorageTypeClientPreservesExactIdentityAndConcurrency(string operation)
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object" });
        var type = new StorageTypeResource
        {
            ApiVersion = ResourceApiVersions.CoreV1, Kind = StorageResourceKinds.StorageType,
            Metadata = new() { Name = "custom", Namespace = ResourceNamespace.Parse("publisher") }, ScopeRef = StorageScope,
            Definition = new() { DisplayName = "Type", PreferredVersion = "1", Versions = [new()
            {
                Version = "1", ConfigurationSchema = schema, SchemaDigest = StorageDigests.Schema(schema),
                EnvelopeSchema = schema, EnvelopeSchemaDigest = StorageDigests.Schema(schema)
            }] }
        };
        using var handler = new StubHandler(request =>
        {
            Assert.AreEqual(operation == "publish" ? "/api/storage-types/custom/versions" : "/api/storage-types/custom", request.RequestUri!.AbsolutePath);
            StringAssert.Contains(request.RequestUri.Query, "namespace=publisher");
            StringAssert.Contains(request.RequestUri.Query, Uri.EscapeDataString(StorageScope.Value));
            Assert.AreEqual(operation switch { "get" => HttpMethod.Get, "update" => HttpMethod.Put, "publish" => HttpMethod.Post, _ => HttpMethod.Delete }, request.Method);
            if (operation != "get") Assert.AreEqual("\"previous\"", request.Headers.IfMatch.Single().ToString());
            return operation == "delete" ? new(HttpStatusCode.NoContent) : new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(type), Headers = { ETag = new EntityTagHeaderValue("\"current\"") }
            };
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://console.test/") };
        IStorageTypesClient client = new StoragesApiClient(http);
        var address = new ScopedResourceAddress(StorageScope, type.Namespace, type.Kind, type.Name);
        if (operation == "delete") { await client.DeleteTypeAsync(address, "\"previous\"", default); return; }
        var snapshot = operation switch
        {
            "get" => await client.GetTypeAsync(address, default),
            "update" => await client.UpdateTypeAsync(address, new(type.Definition), "\"previous\"", default),
            _ => await client.PublishTypeVersionAsync(address, new(type.Definition.Versions.Single()), "\"previous\"", default)
        };
        Assert.AreEqual("\"current\"", snapshot.ETag);
        Assert.AreEqual(type.Namespace, snapshot.Value.Namespace);
    }

    [TestMethod]
    public async Task ResourcePolicyEvaluationUsesCanonicalEndpointAndTypedScopeAction()
    {
        var identity = new ResourceAccessIdentity(Guid.NewGuid(), StorageScope, ResourceNamespace.Default, StorageResourceKinds.StorageType);
        using var handler = new StubHandler(request =>
        {
            Assert.AreEqual($"/api/resource-access-policies/{identity.Uid}/evaluate", request.RequestUri!.AbsolutePath);
            var body = request.Content!.ReadFromJsonAsync<EvaluateResourceAccessRequest>().GetAwaiter().GetResult()!;
            Assert.AreEqual(identity, body.Resource);
            Assert.AreEqual(ResourceScopeRef.Instance, body.ConsumerScopeRef);
            Assert.AreEqual(ResourceAccessAction.Discover, body.Action);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new ResourceAccessDecision(false, ResourceAccessDecisionReasons.ConsumerOutsideOwnerTree)) };
        });
        using var http = new HttpClient(handler) { BaseAddress = new("https://console.test/") };
        var result = await new ResourceAccessPolicyApiClient(http).EvaluateAsync(new(identity, ResourceScopeRef.Instance, ResourceAccessAction.Discover), default);
        Assert.IsFalse(result.Allowed);
        Assert.AreEqual(ResourceAccessDecisionReasons.ConsumerOutsideOwnerTree, result.ReasonCode);
    }
}
