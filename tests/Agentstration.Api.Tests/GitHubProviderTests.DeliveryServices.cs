using System.Text;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Agentstration.Tools;
using Agentstration.Work;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class GitHubProviderTests
{
    internal static async Task<WorkProjectConnectionSelection> InstallDeliveryForgeServiceAsync(IServiceProvider services, RequestContext context,
        string name, bool instanceService, bool alternateAccount, string targetId = "42", string repository = Repository)
    {
        using var system = services.GetRequiredService<IRequestContextScopeFactory>().PushSystem();
        var store = services.GetRequiredService<IResourceStore>(); var workspace = ResourceScopeRef.Workspace(context.WorkspaceId);
        var owner = instanceService ? ResourceScopeRef.Instance : workspace;
        var type = (await store.ListAllAsync<ConnectedServiceTypeResource>(ConnectedServiceResourceKinds.ConnectedServiceType, default))
            .Single(value => value.Value.ScopeRef == owner && value.Value.Name == (instanceService ? "instance-github-type" : "github")).Value;
        var secrets = services.GetRequiredService<SecretManagementService>();
        await secrets.CreateSecretAsync(new SecretResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = SecretResourceKinds.Secret,
            ScopeRef = owner,
            Metadata = new() { Name = name + "-token" },
            Definition = new() { DisplayName = "Disposable delivery fixture", Vault = new(instanceService ? "github-instance-vault" : "git-vault", owner), Key = name }
        }, default);
        using (var token = new SecretValue(Encoding.UTF8.GetBytes(Credential + (alternateAccount ? "-second" : ""))))
            await secrets.SetValueExactAsync(owner, name + "-token", token, default);
        var service = await CreateGitHubServiceAsync(services, context, type,
            alternateAccount ? ServiceEnvelope("8", Credential + "-second", targetId, repository) : ServiceEnvelope(targetId: targetId, repository: repository), name + "-service", name + "-token", serviceOwner: owner);
        if (instanceService)
            await services.GetRequiredService<IResourceAccessPolicyService>().PutAsync(new(new(service.Uid, owner, service.Namespace, service.Kind, service.Generation),
                [new(workspace, [ResourceAccessAction.Discover, ResourceAccessAction.Use])]), null, default);
        var tool = (await services.GetRequiredService<ToolManagementService>().ListVisibleToolsAsync(workspace, default))
            .Single(value => value.Value.Definition.ExternalId == "github.forge.describe").Value;
        return new(new(name, WorkProjectConnectionRules.Forge, "1.0", ["resolve", "read", "create"],
            new(ServiceEnvelope().Inputs["providerId"], targetId)),
            new(name, new(new(owner, service.Namespace, service.Kind, service.Name), service.Uid, service.Generation), Pin(tool),
                WorkProjectConnectionRules.TargetFields.Select(field => new WorkProjectConnectionExport(field, field)).ToArray()));
    }
}
