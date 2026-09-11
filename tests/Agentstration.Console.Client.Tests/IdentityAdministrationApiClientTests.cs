using System.Net;
using System.Net.Http.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class IdentityAdministrationApiClientTests
{
    [TestMethod]
    public async Task ContextAndWorkspaceCreationUsePublicIdentityContracts()
    {
        var principalId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var requests = new List<(HttpMethod Method, string Path)>();
        var handler = new StubHandler(request =>
        {
            requests.Add((request.Method, request.RequestUri!.PathAndQuery));
            if (request.Method == HttpMethod.Get)
            {
                var context = new IdentityConsoleContextResponse(
                    new RequestContext(principalId, tenantId, workspaceId),
                    "Operator",
                    "tenant",
                    "Tenant",
                    "workspace",
                    "Workspace",
                    [AuthorizationPermissions.WorkspacesRead],
                    [new IdentityConsoleWorkspaceResponse(workspaceId, tenantId, "tenant", "Tenant", "workspace", "Workspace", WorkspaceStatus.Active, [AuthorizationPermissions.WorkspacesRead])]);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(context) };
            }

            var workspace = new Workspace(workspaceId, tenantId, "support", "Support", WorkspaceStatus.Active, now);
            return new HttpResponseMessage(HttpStatusCode.Created) { Content = JsonContent.Create(workspace) };
        });
        var client = new IdentityAdministrationApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://console.test/") });

        var contextResponse = await client.GetContextAsync(default);
        var workspaceResponse = await client.CreateWorkspaceAsync("support", "Support", default);

        Assert.AreEqual(principalId, contextResponse.Context.PrincipalId);
        Assert.AreEqual(workspaceId, workspaceResponse.Id);
        CollectionAssert.AreEqual(
            new[] { (HttpMethod.Get, "/api/identity/context"), (HttpMethod.Post, "/api/identity/workspaces") },
            requests);
    }

    [TestMethod]
    public async Task ApiFailuresRetainStatusAndProblemDetails()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = JsonContent.Create(new { title = "permission_denied", detail = "Workspace access is required." })
        });
        var client = new IdentityAdministrationApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://console.test/") });

        var exception = await Assert.ThrowsAsync<AgentstrationApiException>(() => client.GetOrganizationAsync(default));

        Assert.AreEqual(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.AreEqual("permission_denied", exception.ProblemTitle);
        Assert.AreEqual("Workspace access is required.", exception.Message);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request));
        }
    }
}
