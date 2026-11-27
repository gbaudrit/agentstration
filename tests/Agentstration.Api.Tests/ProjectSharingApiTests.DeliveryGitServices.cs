using System.Text;
using System.Text.Json;
using Agentstration.Bootstrap.Contracts;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Agentstration.Tools;
using Agentstration.Work;
using Agentstration.Aep.Client;
using Agentstration.Extensions.Aep;
using Agentstration.Extensions.Contracts;
using Agentstration.Infrastructure.ConnectedServices;
using Agentstration.Testing;
using Agentstration.ResourceManagement.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class ProjectSharingApiTests
{
    private static async Task DeliveryGitServicesAsync(SharingHost host, bool first, AuthenticatedGitFixture native)
    {
        using var system = host.Host.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var services = host.Host.Services; var store = services.GetRequiredService<IResourceStore>();
        var workspace = ResourceScopeRef.Workspace(host.Context.WorkspaceId); var owner = first ? ResourceScopeRef.Instance : workspace;
        var registration = (await store.GetExactAsync<ExtensionRegistrationResource>(new(workspace, default, ExtensionKinds.ExtensionRegistration, "git-fixture"), default))!.Value;
        using var client = new HttpClient(services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("git-service-fixture")) { BaseAddress = new("http://git-fixture/") };
        var manifest = await new AepClient(client, expectedExtensionId: "Agentstration.Extensions.Git").DiscoverAsync();
        foreach (var provisioner in services.GetServices<IAepEnrollmentResourceProvisioner>().OfType<AepEnrollmentConnectedServiceTypeProvisioner>())
            await provisioner.ProvisionAsync(registration, manifest, default);
        var type = (await store.ListAllAsync<ConnectedServiceTypeResource>(ConnectedServiceResourceKinds.ConnectedServiceType, default))
            .Single(value => value.Value.Name == "git").Value;
        if (first) type = (await services.GetRequiredService<ConnectedServiceTypeManagementService>().CreateAsync(new()
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ConnectedServiceResourceKinds.ConnectedServiceType,
            ScopeRef = owner,
            Metadata = new() { Name = "instance-git-type" },
            Definition = type.Definition with { UsePolicy = new() { Grants = [new(workspace)] } }
        }, default)).Value;
        var version = type.Definition.Versions.Single();
        foreach (var name in new[] { "sharing", "code-publication" })
        {
            var secrets = services.GetRequiredService<SecretManagementService>();
            await secrets.CreateSecretAsync(new()
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = SecretResourceKinds.Secret,
                ScopeRef = owner,
                Metadata = new() { Name = name + "-git-token" },
                Definition = new() { DisplayName = "Disposable Git fixture", Vault = new(first ? "github-instance-vault" : "git-vault", owner), Key = name }
            }, default);
            using (var value = new SecretValue(Encoding.UTF8.GetBytes(AuthenticatedGitFixture.Credential)))
                await secrets.SetValueExactAsync(owner, name + "-git-token", value, default);
            var options = JsonSerializer.SerializeToNode(new
            {
                repository = native.Repository,
                authentication = "Basic",
                username = "fixture",
                allowedRefPrefixes = new[] { name == "sharing" ? "refs/heads/agentstration-sharing" : "refs/heads/agentstration/" },
                allowedPathPrefixes = new[] { name == "sharing" ? "shared" : "answer.txt" }
            }, Json)!.AsObject();
            if (name == "sharing") options["refName"] = "refs/heads/agentstration-sharing";
            var service = (await services.GetRequiredService<ConnectedServiceManagementService>().CreateAsync(new()
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ConnectedServiceResourceKinds.ConnectedService,
                ScopeRef = owner,
                Metadata = new() { Name = name + "-git" },
                Definition = new()
                {
                    DisplayName = name,
                    UsePolicy = first ? new() { Grants = [new(workspace)] } : new(),
                    Type = new() { Type = new(type.Name, owner, type.Namespace), TypeUid = type.Uid, Version = version.Version, SchemaDigest = version.SchemaDigest },
                    Options = JsonSerializer.SerializeToElement(options, Json),
                    Bindings = [new() { InputId = "credential", Kind = ConnectedServiceBindingKind.Secret, Secret = new(new(default, "Secret", name + "-git-token"), owner) }]
                }
            }, default)).Value;
            if (first) await services.GetRequiredService<IResourceAccessPolicyService>().PutAsync(new(new(service.Uid, owner, service.Namespace, service.Kind, service.Generation),
                [new(workspace, [ResourceAccessAction.Discover, ResourceAccessAction.Use])]), null, default);
        }
    }
    private static async Task DeliveryGitGrantAsync(SharingHost host, WorkProjectConnectionEvidence proof, bool allowed)
    {
        using var system = host.Host.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var policies = host.Host.Services.GetRequiredService<IResourceAccessPolicyService>();
        var prior = await policies.GetAsync(proof.Service.Uid, default);
        await policies.PutAsync(new(new(proof.Service.Uid, proof.Service.Address.ScopeRef, proof.Service.Address.Namespace,
            proof.Service.Address.Kind, proof.Service.Generation), allowed
                ? [new(ResourceScopeRef.Workspace(host.Context.WorkspaceId), [ResourceAccessAction.Discover, ResourceAccessAction.Use])]
                : [new(ResourceScopeRef.Workspace(host.Context.WorkspaceId), [ResourceAccessAction.Discover])]), prior?.ETag, default);
    }
    private static async Task<BootstrapBindingSelection[]> DeliveryGitBindingsAsync(SharingHost host)
    {
        using var system = host.Host.Services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var services = await host.Host.Services.GetRequiredService<IResourceStore>().ListAllAsync<ConnectedServiceResource>(ConnectedServiceResourceKinds.ConnectedService, default);
        return new[] { "sharing", "code-publication" }.Select(name =>
        {
            var service = services.Single(value => value.Value.Name == name + "-git").Value;
            return new BootstrapBindingSelection("seed", name + "-service", new(service.Name, service.ScopeRef, service.Namespace));
        }).ToArray();
    }
}
