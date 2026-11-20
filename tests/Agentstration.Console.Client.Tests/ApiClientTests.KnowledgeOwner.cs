using System.Net;
using Agentstration.Resources;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;
public sealed partial class ApiClientTests
{
    [TestMethod]
    public async Task KnowledgeOwnerRemainsLocalToTheSelectedClientAndQualifiesChildRequests()
    {
        var paths = new List<string>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            paths.Add(request.RequestUri!.PathAndQuery);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        })) { BaseAddress = new Uri("http://localhost/") };
        var client = new KnowledgeSourcesApiClient(http);
        var selected = client.WithOwner(ResourceScopeRef.Instance);
        Assert.IsNull(await selected.GetExposureAsync(ResourceNamespace.Parse("research"), "sample"));
        Assert.IsNull(await client.GetExposureAsync(ResourceNamespace.Default, "sample"));
        Assert.AreEqual("/api/knowledgesources/sample/tool-exposure?namespace=research&scopeRef=%2Finstance", paths[0]);
        Assert.AreEqual("/api/knowledgesources/sample/tool-exposure", paths[1]);
    }
}
