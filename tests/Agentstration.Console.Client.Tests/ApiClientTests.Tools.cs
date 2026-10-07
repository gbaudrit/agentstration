using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Tools.Contracts;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

public sealed partial class ApiClientTests
{
    [TestMethod]
    public async Task ToolClientRunsNamespacedToolInRequestedMode()
    {
        HttpRequestMessage? captured = null;
        RunToolRequest? payload = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            captured = request;
            payload = request.Content!.ReadFromJsonAsync<RunToolRequest>().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new RunToolResponse(
                    ToolRunMode.Simulate,
                    "simulated",
                    "search",
                    "shared.tools",
                    "utilities",
                    false,
                    false,
                    []))
            };
        })) { BaseAddress = new Uri("http://localhost/") };

        var response = await new ToolsApiClient(httpClient).RunToolAsync(
            new ResourceNamespace("shared.tools"),
            "search",
            new RunToolRequest(JsonSerializer.SerializeToElement(new { query = "agent" })),
            default);

        Assert.IsNotNull(captured);
        Assert.AreEqual(HttpMethod.Post, captured.Method);
        Assert.AreEqual("/api/tools/search/run?namespace=shared.tools", captured.RequestUri!.PathAndQuery);
        Assert.IsNotNull(payload);
        Assert.AreEqual(ToolRunMode.Simulate, payload.Mode);
        Assert.AreEqual("agent", payload.Arguments.GetProperty("query").GetString());
        Assert.IsFalse(response.ProviderInvoked);
    }
}
