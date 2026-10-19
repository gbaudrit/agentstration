using System.Text.Json;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class SecretEditorModelTests
{
    [TestMethod]
    public void EditingVaultPreservesProviderOptionsAndUseGrants()
    {
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var workspace = ResourceScopeRef.Workspace(Guid.NewGuid());
        var vault = new VaultResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = SecretResourceKinds.Vault,
            Metadata = new() { Name = "shared" },
            ScopeRef = tenant,
            Definition = new()
            {
                DisplayName = "Shared",
                ProviderType = "local",
                ProviderOptions = new Dictionary<string, JsonElement> { ["path"] = JsonSerializer.SerializeToElement("test") },
                UsePolicy = new() { Grants = [new(workspace)] }
            }
        };

        var saved = VaultEditorModel.From(vault).Properties();

        Assert.AreEqual("test", saved.ProviderOptions["path"].GetString());
        Assert.AreEqual(workspace, saved.UsePolicy.Grants.Single().ScopeRef);
    }

    [TestMethod]
    public void EditingSecretRetainsExactAncestorVaultReferenceAndUseGrants()
    {
        var tenant = ResourceScopeRef.Tenant(Guid.NewGuid());
        var workspace = ResourceScopeRef.Workspace(Guid.NewGuid());
        var secret = new SecretResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = SecretResourceKinds.Secret,
            Metadata = new() { Name = "credential" },
            ScopeRef = tenant,
            Definition = new()
            {
                DisplayName = "Credential",
                Vault = new("shared", ResourceScopeRef.Instance, new ResourceNamespace("team")),
                Key = "credential",
                UsePolicy = new DescendantUsePolicy { Grants = [new(workspace)] }
            }
        };

        var saved = SecretEditorModel.From(secret).Properties(tenant);

        Assert.AreEqual(ResourceScopeRef.Instance, saved.Vault.ScopeRef);
        Assert.AreEqual(new ResourceNamespace("team"), saved.Vault.Namespace);
        Assert.AreEqual(workspace, saved.UsePolicy.Grants.Single().ScopeRef);
    }
}
