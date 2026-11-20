using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Agentstration.Agents;
using Agentstration.Api.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

public sealed partial class ApiClientTests
{
    [TestMethod]
    public async Task ManagementAgentPageUsesServerOffsetsAndReportsMissingBoundedDeploymentAsUnknown()
    {
        var requests = new ConcurrentQueue<string>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            requests.Enqueue(request.RequestUri!.PathAndQuery);
            return new(HttpStatusCode.OK)
            {
                Content = request.RequestUri.AbsolutePath == "/api/agents"
                ? JsonContent.Create(new PagedResponse<AgentResource>([CreateAgentResource("paged") with { ScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()) }], "/api/agents?allNamespaces=true&skip=60&top=20"))
                : JsonContent.Create(new PagedResponse<AgentDeployment>([], "/api/deployments?skip=1000&top=1000"))
            };
        }))
        { BaseAddress = new("https://agentstration.test/") };
        var page = await new ManagementApiClient(http).GetAgentsPageAsync(40, 20, default);
        Assert.IsTrue(page.HasMore);
        Assert.IsFalse(page.DeploymentWindowComplete);
        Assert.AreEqual("Unknown", page.Items[0].Runtime);
        Assert.IsNull(page.Items[0].DeploymentId);
        Assert.AreEqual(DateTimeOffset.MinValue, page.Items[0].LastActivity);
        CollectionAssert.AreEquivalent(new[] { "/api/agents?allNamespaces=true&skip=40&top=20", "/api/deployments?top=1000" }, requests.ToArray());
    }

    [TestMethod]
    public async Task ManagementAgentPageDoesNotAssociateSameNamedDeploymentFromAnotherOwner()
    {
        var agent = CreateAgentResource("same") with { ScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()) };
        var deployment = new AgentDeployment
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = AgentResourceKinds.AgentDeployment,
            Metadata = new() { Name = "same--g000001" },
            ScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()),
            AgentNamespace = agent.Namespace,
            AgentName = "same",
            RevisionName = "same--000001",
            Environment = "local",
            RuntimeProfileName = "maf-builtin",
            HostingMode = AgentHostingMode.InProcess,
            DesiredState = DesiredAgentState.Running,
            ProvisioningState = ProvisioningState.Succeeded,
            OperationalState = OperationalState.Ready,
            UpdatedAt = DateTimeOffset.UnixEpoch
        };
        using var http = new HttpClient(new StubHandler(request => new(HttpStatusCode.OK)
        {
            Content = request.RequestUri!.AbsolutePath == "/api/agents"
            ? JsonContent.Create(new PagedResponse<AgentResource>([agent], null)) : JsonContent.Create(new PagedResponse<AgentDeployment>([deployment], null))
        }))
        { BaseAddress = new("https://agentstration.test/") };
        var page = await new ManagementApiClient(http).GetAgentsPageAsync(0, 20, default);
        Assert.IsTrue(page.DeploymentWindowComplete);
        Assert.AreEqual("Unknown", page.Items[0].Runtime);
        Assert.IsNull(page.Items[0].DeploymentUrl);
    }
}
