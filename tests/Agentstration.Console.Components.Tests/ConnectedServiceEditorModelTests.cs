using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ConnectedServiceEditorModelTests
{
    [TestMethod]
    public void PropertiesPinContributionAndPersistExactManualBindings()
    {
        var owner = ResourceScopeRef.Workspace(Guid.NewGuid());
        var parameterScope = ResourceScopeRef.Tenant(Guid.NewGuid());
        var type = new ConnectedServiceTypeResource
        {
            Uid = Guid.NewGuid(),
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ConnectedServiceResourceKinds.ConnectedServiceType,
            Metadata = new() { Name = "redis", Namespace = new("azure.extension") },
            ScopeRef = ResourceScopeRef.Instance,
            Definition = new() { DisplayName = "Azure Cache for Redis", PreferredVersion = "1.0.0" }
        };
        var version = new ConnectedServiceTypeVersionDescriptor("1.0.0", "sha256:contract",
            JsonSerializer.SerializeToElement(new { type = "object" }), [], []);
        var editor = new ConnectedServiceEditorModel
        {
            Name = "redis",
            DisplayName = "Redis",
            Bindings =
            [
                new() { InputId = "endpoint", Kind = ConnectedServiceBindingKind.Parameter, Reference = $"{parameterScope.Value}|shared|endpoint" },
                new() { InputId = "retries", Kind = ConnectedServiceBindingKind.Literal, Literal = JsonSerializer.SerializeToElement(3) }
            ],
            UseGrants = [new DescendantUseGrant(owner)]
        };

        var properties = editor.Properties(type, version);

        Assert.AreEqual(type.Uid, properties.Type.TypeUid);
        Assert.AreEqual("redis", properties.Type.Type.Name);
        Assert.AreEqual(new ResourceNamespace("azure.extension"), properties.Type.Type.Namespace);
        Assert.AreEqual("sha256:contract", properties.Type.SchemaDigest);
        var parameter = properties.Bindings.Single(value => value.InputId == "endpoint");
        Assert.AreEqual(parameterScope, parameter.Parameter!.ScopeRef);
        Assert.AreEqual(new ResourceNamespace("shared"), parameter.Parameter.Address.Namespace);
        Assert.IsNull(parameter.Secret);
        var literal = properties.Bindings.Single(value => value.InputId == "retries");
        Assert.AreEqual(ConnectedServiceBindingKind.Literal, literal.Kind);
        Assert.AreEqual(3, literal.Literal?.GetInt32());
        Assert.IsNull(literal.Parameter);
        Assert.IsNull(literal.Secret);
        Assert.AreEqual(owner, properties.UsePolicy.Grants.Single().ScopeRef);
    }
}
