using Agentstration.Models;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Web.Console;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ParameterEditorModelTests
{
    [TestMethod]
    public void ParameterEditorPersistsTypedVisibleValueAndUseGrants()
    {
        var workspace = ResourceScopeRef.Workspace(Guid.NewGuid());
        var editor = new ParameterEditorModel
        {
            Name = "retry-count",
            DisplayName = "Retry count",
            ValueType = ParameterValueType.WholeNumber,
            Value = "12",
            UseGrants = [new DescendantUseGrant(workspace)]
        };

        var properties = editor.Properties();

        Assert.AreEqual(12L, properties.Value.GetInt64());
        Assert.AreEqual(workspace, properties.UsePolicy.Grants.Single().ScopeRef);
    }

    [TestMethod]
    public void ModelProviderEditorPersistsExactParameterAndSecretBindings()
    {
        var parameterScope = ResourceScopeRef.Tenant(Guid.NewGuid());
        var secretScope = ResourceScopeRef.Workspace(Guid.NewGuid());
        var editor = new ModelProviderEditorModel
        {
            Name = "provider",
            DisplayName = "Provider",
            ExtensionId = "default:extension",
            ContributionId = "provider",
            ValueBindings =
            [
                new() { RequirementId = "endpoint", Kind = ModelProviderValueBindingKind.Parameter, Name = "endpoint", Namespace = "shared", ScopeRef = parameterScope.Value },
                new() { RequirementId = "credential", Kind = ModelProviderValueBindingKind.Secret, Name = "api-key", Namespace = "default", ScopeRef = secretScope.Value }
            ]
        };

        var bindings = editor.ToProperties().ValueBindings;

        Assert.AreEqual(parameterScope, bindings[0].Parameter!.ScopeRef);
        Assert.AreEqual(new ResourceNamespace("shared"), bindings[0].Parameter!.Address.Namespace);
        Assert.AreEqual(secretScope, bindings[1].Secret!.ScopeRef);
        Assert.AreEqual("api-key", bindings[1].Secret!.Address.Name);
    }
}
