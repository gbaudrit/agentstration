extern alias GitHubExtension;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Agentstration.Aep.Client;
using Agentstration.Application.Work;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Extensions.Aep;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.ConnectedServices;
using Agentstration.Infrastructure.Work;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Agentstration.Tools;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Microsoft.Extensions.DependencyInjection;
using GitHub = GitHubExtension::Agentstration.Extensions.GitHub;

namespace Agentstration.Web.Tests;

public sealed partial class GitHubProviderTests
{
    [TestMethod]
    public async Task NamedGitHubServicesUseRealProtectedResolverAndRetainOriginalOperationAcrossHostRestart()
    {
        using var fixture = new Fixture(); await using var extension = ContractExtension(fixture);
        var directory = Path.Combine(fixture.Directory, "named-host"); var id = Guid.NewGuid(); var operation = Guid.NewGuid();
        var need = new WorkProjectConnectionRequirement("tracker", WorkProjectConnectionRules.Tracking, "1.0", ["read", "create"],
            new(GitHub.GitHubServiceContract.Authority("https://api.github.com"), "42"));
        WorkProjectResourceReference retainedService;
        await using (var host = CodingFactory(directory, extension))
        {
            using var http = host.CreateClient();
            var context = await host.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
            using var scope = host.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
            _ = await InstallGitHubRelaysAsync(host.Services, context);
            var store = host.Services.GetRequiredService<IResourceStore>(); var owner = ResourceScopeRef.Workspace(context.WorkspaceId);
            var registration = (await store.GetExactAsync<ExtensionRegistrationResource>(new(owner, default, ExtensionKinds.ExtensionRegistration, "github-fixture"), default))!.Value;
            using var descriptorClient = extension.CreateClient();
            var manifest = await new AepClient(descriptorClient, expectedExtensionId: "Agentstration.Extensions.GitHub").DiscoverAsync();
            foreach (var provisioner in host.Services.GetServices<IAepEnrollmentResourceProvisioner>().OfType<AepEnrollmentConnectedServiceTypeProvisioner>()) await provisioner.ProvisionAsync(registration, manifest, default);
            var type = (await store.ListAllAsync<ConnectedServiceTypeResource>(ConnectedServiceResourceKinds.ConnectedServiceType, default))
                .Single(value => value.Value.Name == "github").Value;
            var version = type.Definition.Versions.Single();
            var mappings = WorkProjectConnectionRules.TargetFields.Select(field => new WorkProjectConnectionExport(field, field)).ToArray();
            var tools = await host.Services.GetRequiredService<ToolManagementService>().ListVisibleToolsAsync(owner, default);
            var trackingTool = tools.Single(value => value.Value.Definition.ExternalId == "github.tracking.describe").Value;
            var forgeTool = tools.Single(value => value.Value.Definition.ExternalId == "github.forge.describe").Value;
            var service = await CreateGitHubServiceAsync(host.Services, context, type, ServiceEnvelope(), "primary", "github-token");
            retainedService = new(new(owner, service.Namespace, service.Kind, service.Name), service.Uid, service.Generation);
            var secrets = host.Services.GetRequiredService<SecretManagementService>();
            await secrets.CreateSecretAsync(new SecretResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = "Secret",
                Metadata = new() { Name = "second-token" },
                ScopeRef = owner,
                Definition = new() { DisplayName = "Second disposable fixture token", Vault = new("github-vault", owner), Key = "second-token" }
            }, default);
            using (var value = new SecretValue(Encoding.UTF8.GetBytes(Credential + "-second"))) await secrets.SetValueExactAsync(owner, "second-token", value, default);
            var second = await CreateGitHubServiceAsync(host.Services, context, type, ServiceEnvelope("8", Credential + "-second", "43", "fixture/second"), "secondary", "second-token");
            var alternateNeed = need with { Name = "alternate", Target = new(need.Target!.ProviderId, "43") };
            var alternateAssignment = new WorkProjectConnectionAssignment("alternate", new(new(owner, second.Namespace, second.Kind, second.Name), second.Uid, second.Generation), Pin(trackingTool), mappings);
            var assignment = new WorkProjectConnectionAssignment("tracker", retainedService, Pin(trackingTool), mappings);
            var forge = new WorkProjectConnectionRequirement("repository", WorkProjectConnectionRules.Forge, "1.0", ["resolve", "read"], need.Target);
            using var created = await http.PostAsJsonAsync("/api/workspaces/default/projects", new CreateWorkProjectRequest(id, Guid.NewGuid(), "named-github",
                new("Named GitHub", null, [], []) { ConnectionRequirements = [need, forge, alternateNeed], ConnectionAssignments = [assignment, assignment with { Name = "repository", DescribeTool = Pin(forgeTool) }, alternateAssignment] }));
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
            var incompatibleVersion = version with { Inputs = version.Inputs.Select(input => input.Id == "credential" ? input with { Protection = ConnectedServiceValueProtection.Standard } : input).ToArray() };
            var incompatibleType = (await host.Services.GetRequiredService<ConnectedServiceTypeManagementService>().CreateAsync(new()
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = "ConnectedServiceType",
                ScopeRef = owner,
                Metadata = new() { Name = "incompatible-github" },
                Definition = new() { DisplayName = "Incompatible fixture", PreferredVersion = version.Version, Versions = [incompatibleVersion] }
            }, default)).Value;
            var incompatibleService = await CreateGitHubServiceAsync(host.Services, context, incompatibleType, ServiceEnvelope(), "incompatible", "github-token", true);
            using var incompatible = await http.PostAsJsonAsync("/api/workspaces/default/projects", new CreateWorkProjectRequest(Guid.NewGuid(), Guid.NewGuid(), "incompatible",
                new("Incompatible", null, [], []) { ConnectionRequirements = [need], ConnectionAssignments = [assignment with { Service = new(new(owner, default, "ConnectedService", incompatibleService.Name), incompatibleService.Uid, incompatibleService.Generation) }] }));
            Assert.AreEqual(HttpStatusCode.BadRequest, incompatible.StatusCode, await incompatible.Content.ReadAsStringAsync());
            Assert.IsFalse((await incompatible.Content.ReadAsStringAsync()).Contains(Credential, StringComparison.Ordinal));
            var contextResponse = (await http.GetFromJsonAsync<WorkProjectContextResponse>($"/api/workspaces/default/projects/{id}/context"))!;
            Assert.IsTrue(contextResponse.Connections.All(value => value.Available));
            Assert.IsFalse(JsonSerializer.Serialize(contextResponse).Contains(Credential, StringComparison.Ordinal));
            var proof = contextResponse.Connections.Single(value => value.Requirement.Name == "tracker").Service!;
            using (var headers = await ((WorkProjectBindingResolver)host.Services.GetRequiredService<IWorkProjectBindingResolver>()).ResolveConnectionHeadersAsync(
                new(context.WorkspaceId), context.PrincipalId, new(need, assignment), proof, default))
            {
                var encoded = headers.Value.GetProperty(WorkProjectConnectedServiceResolver.TransportHeader).GetString()!;
                var protectedOutput = headers.Protect(JsonSerializer.SerializeToElement(new { token = Credential, envelope = encoded }))!.Value.GetRawText();
                Assert.IsFalse(protectedOutput.Contains(Credential, StringComparison.Ordinal)); Assert.IsFalse(protectedOutput.Contains(encoded, StringComparison.Ordinal));
            }
            fixture.Native.LoseIssueResponse = true;
            using var began = await http.PostAsJsonAsync($"/api/workspaces/default/projects/{id}/task-operations", new BeginWorkTaskOperationRequest(operation, 1, "tracker",
                new(ExternalTaskOperationKind.Publish, Title: "Named service issue") { Tracking = need.Target }));
            Assert.AreEqual(HttpStatusCode.Accepted, began.StatusCode, await began.Content.ReadAsStringAsync());
            var receipt = (await began.Content.ReadFromJsonAsync<WorkTaskOperationResponse>())!;
            Assert.AreEqual(WorkTaskOperationStatus.Uncertain, receipt.Status, receipt.ErrorCode);
            Assert.AreEqual(retainedService, receipt.Route.Tracking!.Connection.Service!.Service);
        }
        await using (var host = CodingFactory(directory, extension))
        {
            using var http = host.CreateClient();
            using var resumed = await http.PostAsync($"/api/workspaces/default/projects/{id}/task-operations/{operation}/resume", null);
            Assert.AreEqual(HttpStatusCode.Accepted, resumed.StatusCode, await resumed.Content.ReadAsStringAsync());
            var receipt = (await resumed.Content.ReadFromJsonAsync<WorkTaskOperationResponse>())!;
            Assert.AreEqual(WorkTaskOperationStatus.Succeeded, receipt.Status, receipt.ErrorCode);
            Assert.AreEqual(retainedService, receipt.Route.Tracking!.Connection.Service!.Service);
            Assert.AreEqual(1, fixture.Native.IssuePosts);
            var context = await host.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
            using var scope = host.Services.GetRequiredService<IRequestContextScopeFactory>().Push(context);
            using (var denied = new SecretValue(Encoding.UTF8.GetBytes("denied")))
                await host.Services.GetRequiredService<SecretManagementService>().SetValueExactAsync(ResourceScopeRef.Workspace(context.WorkspaceId), "github-token", denied, default);
            var availability = (await http.GetFromJsonAsync<WorkProjectContextResponse>($"/api/workspaces/default/projects/{id}/context"))!;
            Assert.IsTrue(availability.Connections.Where(value => value.Requirement.Name != "alternate").All(value => !value.Available));
            Assert.IsTrue(availability.Connections.Single(value => value.Requirement.Name == "alternate").Available); Assert.AreEqual(1, fixture.Native.IssuePosts);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private static async Task<ConnectedServiceResource> CreateGitHubServiceAsync(IServiceProvider services, RequestContext context,
        ConnectedServiceTypeResource type, GitHub.GitHubServiceEnvelope envelope, string name, string secretName, bool unsafeCredential = false)
    {
        var version = type.Definition.Versions.Single(); var owner = ResourceScopeRef.Workspace(context.WorkspaceId);
        var bindings = envelope.Inputs.Where(pair => pair.Key != "credential").Select(pair => new ConnectedServiceValueBinding
        { InputId = pair.Key, Kind = ConnectedServiceBindingKind.Literal, Literal = JsonSerializer.SerializeToElement(pair.Value) }).ToList();
        bindings.Add(unsafeCredential ? new() { InputId = "credential", Kind = ConnectedServiceBindingKind.Literal, Literal = JsonSerializer.SerializeToElement(Credential) }
            : new() { InputId = "credential", Kind = ConnectedServiceBindingKind.Secret, Secret = new(new(default, "Secret", secretName), owner) });
        return (await services.GetRequiredService<ConnectedServiceManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ConnectedServiceResourceKinds.ConnectedService,
            Metadata = new() { Name = name },
            ScopeRef = owner,
            Definition = new()
            {
                DisplayName = name,
                Options = envelope.Options,
                Bindings = bindings,
                Type = new() { Type = new(type.Name, type.ScopeRef, type.Namespace), TypeUid = type.Uid, Version = version.Version, SchemaDigest = version.SchemaDigest }
            }
        }, default)).Value;
    }
}
