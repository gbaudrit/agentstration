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

    [TestMethod]
    public void ModelProviderEditorRoundTripPreservesSpecificationOverrides()
    {
        var resource = new ModelProviderResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ModelResourceKinds.ModelProvider,
            Metadata = new ResourceMetadata { Name = "provider", Namespace = ResourceNamespace.Default },
            Definition = new ModelProviderProperties
            {
                DisplayName = "Provider",
                Extension = new ResourceReference("extension"),
                ContributionId = "provider",
                SpecificationOverrides = new Dictionary<string, ModelSpecificationOverride>(StringComparer.Ordinal)
                {
                    ["exact-model"] = new() { Limits = new ModelLimitOverrides { ContextTokens = 8_192 } }
                }
            }
        };

        var properties = ModelProviderEditorModel.FromResource(resource).ToProperties();

        Assert.AreEqual(8_192, properties.SpecificationOverrides["exact-model"].Limits.ContextTokens);
    }

    [TestMethod]
    public void ModelSpecificationOverrideEditorRoundTripsTypedOperations()
    {
        var editor = new ModelSpecificationOverrideEditorModel
        {
            ToolsSupport = ModelFeatureSupport.Native,
            ContextTokens = 32_000
        };
        editor.Input[ModelContentType.Image] = ModelOverrideOperation.Add;
        editor.Output[ModelContentType.Audio] = ModelOverrideOperation.Remove;
        editor.ToolModes[ModelToolMode.Parallel] = ModelOverrideOperation.Add;
        editor.OutputFormats[ModelStructuredOutputFormat.JsonSchema] = ModelOverrideOperation.Add;
        editor.OutputFormatStrict[ModelStructuredOutputFormat.JsonSchema] = true;
        editor.ReasoningEfforts[ReasoningEffort.High] = ModelOverrideOperation.Remove;

        var roundTrip = ModelSpecificationOverrideEditorModel.From(editor.ToOverride());

        Assert.AreEqual(ModelOverrideOperation.Add, roundTrip.Input[ModelContentType.Image]);
        Assert.AreEqual(ModelOverrideOperation.Remove, roundTrip.Output[ModelContentType.Audio]);
        Assert.AreEqual(ModelOverrideOperation.Add, roundTrip.ToolModes[ModelToolMode.Parallel]);
        Assert.AreEqual(true, roundTrip.OutputFormatStrict[ModelStructuredOutputFormat.JsonSchema]);
        Assert.AreEqual(ModelOverrideOperation.Remove, roundTrip.ReasoningEfforts[ReasoningEffort.High]);
        Assert.AreEqual(32_000, roundTrip.ContextTokens);
    }
}
