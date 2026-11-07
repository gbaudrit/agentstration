using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Models;
using Agentstration.Parameters;
using Agentstration.Resources;

namespace Agentstration.ModelProviders.Tests;

[TestClass]
public sealed class LiteralValueBindingTests
{
    [TestMethod]
    [DataRow("\"text\"", AepValueType.Text)]
    [DataRow("12", AepValueType.WholeNumber)]
    [DataRow("1.25", AepValueType.DecimalNumber)]
    [DataRow("false", AepValueType.Logical)]
    public void LiteralRoundTripsAsTypedJsonAndDoesNotPrintItsValue(string json, AepValueType type)
    {
        var literal = JsonSerializer.Deserialize<JsonElement>(json);
        var binding = ModelProviderValueBinding.FromLiteral("setting", literal);
        var roundTrip = JsonSerializer.Deserialize<ModelProviderValueBinding>(JsonSerializer.Serialize(binding))!;

        Assert.AreEqual("setting=Literal:[REDACTED]", roundTrip.ToString());
        Assert.IsTrue(JsonElement.DeepEquals(literal, roundTrip.Literal!.Value));
        Assert.IsEmpty(ModelProviderValueBindingValidator.Validate([roundTrip],
            [new(AepContributionKinds.ModelProvider, "test", "setting", true, type)], "test", true));
    }

    [TestMethod]
    public void LiteralRejectsSecuredRequirementsInvalidTypesAllowedValuesAndOversizedPayloads()
    {
        var requirement = new AepValueRequirement(AepContributionKinds.ModelProvider, "test", "setting", true, AepValueType.Text,
            AllowedValues: [JsonSerializer.SerializeToElement("allowed")]);
        AssertInvalid(ModelProviderValueBinding.FromLiteral("setting", JsonSerializer.SerializeToElement("other")), requirement, "bound_value_not_allowed");
        AssertInvalid(ModelProviderValueBinding.FromLiteral("setting", JsonSerializer.SerializeToElement(3)), requirement, "bound_value_type_invalid");
        AssertInvalid(ModelProviderValueBinding.FromLiteral("setting", JsonSerializer.SerializeToElement("allowed")),
            requirement with { Protection = AepValueProtection.Secured, AllowedValues = null }, "bound_value_protection_invalid");
        AssertInvalid(ModelProviderValueBinding.FromLiteral("setting", JsonSerializer.SerializeToElement(new string('x', AepBoundValueValidator.MaximumInlineValueBytes))),
            requirement with { AllowedValues = null }, "bound_value_too_large");
        AssertInvalid(ModelProviderValueBinding.FromLiteral("setting", JsonSerializer.SerializeToElement<object?>(null)), requirement, "bound_value_type_invalid");
        AssertInvalid(new() { RequirementId = "setting", Kind = ModelProviderValueBindingKind.Literal }, requirement, "value_binding_literal_invalid");
    }

    [TestMethod]
    [DataRow("{}")]
    [DataRow("[]")]
    [DataRow("null")]
    public void ScalarModelProviderContractsRejectStructuredAndNullLiterals(string json)
    {
        var requirement = new AepValueRequirement(AepContributionKinds.ModelProvider, "test", "setting", true, AepValueType.Text);
        AssertInvalid(ModelProviderValueBinding.FromLiteral("setting", JsonSerializer.Deserialize<JsonElement>(json)), requirement, "bound_value_type_invalid");
    }

    [TestMethod]
    public void BindingVariantsRejectMixedLiteralAndResourcePayloads()
    {
        var requirement = new AepValueRequirement(AepContributionKinds.ModelProvider, "test", "setting", true, AepValueType.Text);
        var parameter = new ParameterReference(new(ResourceNamespace.Default, "Parameter", "setting"), ResourceScopeRef.Instance);
        AssertInvalid(ModelProviderValueBinding.FromLiteral("setting", JsonSerializer.SerializeToElement("text")) with { Parameter = parameter },
            requirement, "value_binding_literal_invalid");
        AssertInvalid(ModelProviderValueBinding.FromParameter("setting", parameter) with { Literal = JsonSerializer.SerializeToElement("text") },
            requirement, "value_binding_reference_invalid");
    }

    private static void AssertInvalid(ModelProviderValueBinding binding, AepValueRequirement requirement, string code)
    {
        var issues = ModelProviderValueBindingValidator.Validate([binding], [requirement], "test", true);
        Assert.IsTrue(issues.Any(value => value.Code == code));
    }
}
