using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Security.AspNetCoreIdentity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

public sealed partial class SecurityApiTests
{
    [TestMethod]
    public async Task ProtectedAgentEndpointReturns401WithoutAuthentication()
    {
        await using var factory = Factory("Disabled");
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/agents");
        using var flowRuns = await client.GetAsync("/api/flowRuns");
        using var workplaceSubmission = await client.PostAsJsonAsync(
            "/api/workspaces/personal/entries/request/interactions",
            new { workspaceId = "personal", values = new Dictionary<string, object>() });
        using var externalIdentities = await client.GetAsync($"/api/identity/principals/{Guid.NewGuid():D}/external-identities");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, flowRuns.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, workplaceSubmission.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, externalIdentities.StatusCode);
    }

    [TestMethod]
    public async Task ProtectedAgentEndpointAllowsAuthorizedPrincipalAndRejectsMissingPermission()
    {
        await using var factory = Factory("Development");
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<IIdentityStore>();
        var context = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);

        using var allowed = await client.GetAsync("/api/agents");
        using var allowedRuns = await client.GetAsync("/api/flowRuns");
        Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, allowedRuns.StatusCode);

        foreach (var assignment in await store.ListRoleAssignmentsAsync(context.TenantId, context.PrincipalId, default))
            await store.RemoveRoleAssignmentAsync(assignment.Id, default);
        using var denied = await client.GetAsync("/api/agents");
        using var deniedRuns = await client.GetAsync("/api/flowRuns");
        Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, deniedRuns.StatusCode);
    }

    [TestMethod]
    public async Task WorkspacePolicyIsResourceBasedAndDoesNotCrossWorkspaceBoundary()
    {
        await using var factory = Factory("Development");
        using var client = factory.CreateClient();
        var context = await factory.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        var store = factory.Services.GetRequiredService<IIdentityStore>();

        using var createdResponse = await client.PostAsJsonAsync("/api/identity/workspaces", new { name = "workspace-b", displayName = "Workspace B" });
        Assert.AreEqual(HttpStatusCode.Created, createdResponse.StatusCode);
        var workspaceB = await createdResponse.Content.ReadFromJsonAsync<Workspace>();
        Assert.IsNotNull(workspaceB);

        foreach (var assignment in await store.ListRoleAssignmentsAsync(context.TenantId, context.PrincipalId, default))
            await store.RemoveRoleAssignmentAsync(assignment.Id, default);
        var reader = new RoleDefinition(Guid.NewGuid(), "Security-test-reader", "Security test reader", [AuthorizationPermissions.WorkspacesRead], false);
        await store.AddRoleDefinitionAsync(reader, default);
        await store.AddRoleAssignmentAsync(new RoleAssignment(Guid.NewGuid(), context.TenantId, context.PrincipalId, PrincipalType.User, reader.Id, AuthorizationScopes.Workspace(context.WorkspaceId)), default);

        using var ownWorkspace = await client.GetAsync($"/api/identity/workspaces/{context.WorkspaceId:D}");
        using var otherWorkspace = await client.GetAsync($"/api/identity/workspaces/{workspaceB.Id:D}");

        Assert.AreEqual(HttpStatusCode.OK, ownWorkspace.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, otherWorkspace.StatusCode);
    }

}

