using System.Text.Json;
using Agentstration.Web.Components.JsonSchema;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Components.Tests;

[TestClass]
public sealed class JsonSchemaInputEditorTests
{
    [TestMethod]
    public void GeneratedFieldsValidateAndRoundTripThroughRawJson()
    {
        using var context = Context();
        JsonElement value = default;
        JsonSchemaInputValidationState? validation = null;
        var rendered = context.Render<JsonSchemaInputEditor>(parameters => parameters
            .Add(component => component.Schema, Json("""{"type":"object","properties":{"name":{"type":"string","description":"Display name"},"mode":{"type":"string","enum":["fast","safe"]}},"required":["name"],"additionalProperties":false}"""))
            .Add(component => component.Value, Json("{}"))
            .Add(component => component.ValueChanged, next => value = next)
            .Add(component => component.ValidationChanged, next => validation = next));

        rendered.WaitForAssertion(() => Assert.IsFalse(validation?.IsValid));
        Assert.HasCount(1, rendered.FindAll(".field-validation-error"));
        rendered.Find("input[type=text]").Input("Agentstration");
        rendered.Find("select").Change("\"safe\"");
        rendered.WaitForAssertion(() => Assert.IsTrue(validation?.IsValid));
        Assert.AreEqual("Agentstration", value.GetProperty("name").GetString());
        Assert.AreEqual("safe", value.GetProperty("mode").GetString());

        rendered.Find("[data-testid='schema-raw-mode']").Click();
        var textarea = rendered.Find("textarea");
        StringAssert.Contains(textarea.GetAttribute("value") ?? textarea.TextContent, "Agentstration");
        textarea.Input("not-json");
        rendered.WaitForAssertion(() => Assert.IsFalse(validation?.IsValid));
        textarea.Input("{\"name\":\"Restored\",\"mode\":\"fast\"}");
        rendered.WaitForAssertion(() => Assert.IsTrue(validation?.IsValid));
        rendered.Find("[data-testid='schema-form-mode']").Click();
        Assert.AreEqual("Restored", rendered.Find("input[type=text]").GetAttribute("value"));
    }

    [TestMethod]
    public void SingularMinimumLengthUsesNaturalLocalizedGuidance()
    {
        using var context = Context();
        var rendered = context.Render<JsonSchemaInputEditor>(parameters => parameters
            .Add(component => component.Schema, Json("""{"type":"object","properties":{"prompt":{"type":"string","minLength":1}},"required":["prompt"]}"""))
            .Add(component => component.Value, Json("""{"prompt":""}""")));

        var guidance = rendered.Find(".field-validation-error").TextContent;

        Assert.IsTrue(
            guidance.Contains("one character", StringComparison.Ordinal)
            || guidance.Contains("un caractère", StringComparison.Ordinal));
        Assert.DoesNotContain("1 characters", guidance, StringComparison.Ordinal);
        Assert.DoesNotContain("1 caractères", guidance, StringComparison.Ordinal);
    }

    [TestMethod]
    public void LongTextUsesMultilineEditorAndExposesInputRules()
    {
        using var context = Context();
        var rendered = context.Render<JsonSchemaInputEditor>(parameters => parameters
            .Add(component => component.Schema, Json("""{"type":"object","properties":{"prompt":{"type":"string","minLength":1,"maxLength":16000},"test":{"type":"string"},"reference":{"type":"string","pattern":"^[A-Z]{3}-[0-9]{4}$"}},"required":["prompt"]}"""))
            .Add(component => component.Value, Json("{}")));

        var prompt = rendered.Find("[data-schema-path='$.prompt']");

        Assert.HasCount(1, prompt.QuerySelectorAll("textarea.schema-long-text"));
        Assert.AreEqual("16000", prompt.QuerySelector("textarea")?.GetAttribute("maxlength"));
        StringAssert.Contains(prompt.QuerySelector(".metric-help")?.GetAttribute("aria-label"), "16");
        Assert.HasCount(1, rendered.Find("[data-schema-path='$.test']").QuerySelectorAll("input[type=text]"));
        var patternHelp = rendered.Find("[data-schema-path='$.reference'] .metric-help");
        StringAssert.Contains(patternHelp.ClassName, "metric-help-start");
        StringAssert.Contains(patternHelp.GetAttribute("aria-label"), "^[A-Z]{3}-[0-9]{4}$");
    }

    [TestMethod]
    public void UnsupportedSemanticSchemaUsesExplicitRawFallback()
    {
        using var context = Context();
        JsonSchemaInputValidationState? validation = null;
        var rendered = context.Render<JsonSchemaInputEditor>(parameters => parameters
            .Add(component => component.Schema, Json("""{"type":"object","oneOf":[{"properties":{"name":{"type":"string"}}}]}"""))
            .Add(component => component.Value, Json("{}"))
            .Add(component => component.ValidationChanged, next => validation = next));

        rendered.WaitForAssertion(() => Assert.IsTrue(validation?.UsesRawFallback));
        Assert.HasCount(1, rendered.FindAll("textarea"));
        Assert.IsFalse(string.IsNullOrWhiteSpace(rendered.Find("[role=status]").TextContent));
    }

    [TestMethod]
    public void RawJsonCanReturnToFormWhileSchemaValidationIsInvalid()
    {
        using var context = Context();
        JsonSchemaInputValidationState? validation = null;
        var rendered = context.Render<JsonSchemaInputEditor>(parameters => parameters
            .Add(component => component.Schema, Json("""{"type":"object","properties":{"goal":{"type":"string"}},"required":["goal"]}"""))
            .Add(component => component.Value, Json("{}"))
            .Add(component => component.ValidationChanged, next => validation = next));

        rendered.WaitForAssertion(() => Assert.IsFalse(validation?.IsValid));
        rendered.Find("[data-testid='schema-raw-mode']").Click();
        Assert.HasCount(1, rendered.FindAll("textarea"));

        rendered.Find("[data-testid='schema-form-mode']").Click();

        Assert.HasCount(0, rendered.FindAll("textarea"));
        Assert.HasCount(1, rendered.FindAll("[data-schema-path='$.goal'] input"));
    }

    [TestMethod]
    public void EmptyInputCreatesSchemaTemplateWhenRawModeOpens()
    {
        using var context = Context();
        JsonElement value = default;
        var rendered = context.Render<JsonSchemaInputEditor>(parameters => parameters
            .Add(component => component.Schema, Json("""{"type":"object","properties":{"prompt":{"type":"string"},"attempts":{"type":"integer","default":2},"options":{"type":"object","properties":{"enabled":{"type":"boolean"}}}},"required":["prompt"]}"""))
            .Add(component => component.Value, Json("{}"))
            .Add(component => component.ValueChanged, next => value = next));

        rendered.Find("[data-testid='schema-raw-mode']").Click();

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(string.Empty, value.GetProperty("prompt").GetString());
            Assert.AreEqual(2, value.GetProperty("attempts").GetInt32());
            Assert.IsFalse(value.TryGetProperty("options", out _));
        });
        var raw = rendered.Find("textarea").GetAttribute("value") ?? rendered.Find("textarea").TextContent;
        StringAssert.Contains(raw, "\"prompt\": \"\"");
    }

    [TestMethod]
    public void OptionalConstrainedTextDoesNotBlockUntilItIsEntered()
    {
        using var context = Context();
        JsonElement value = default;
        JsonSchemaInputValidationState? validation = null;
        var rendered = context.Render<JsonSchemaInputEditor>(parameters => parameters
            .Add(component => component.Schema, Json("""{"type":"object","properties":{"test":{"type":"string","minLength":3}},"additionalProperties":false}"""))
            .Add(component => component.Value, Json("{}"))
            .Add(component => component.ValueChanged, next => value = next)
            .Add(component => component.ValidationChanged, next => validation = next));

        rendered.WaitForAssertion(() => Assert.IsTrue(validation?.IsValid));
        Assert.IsFalse(value.TryGetProperty("test", out _));

        var input = rendered.Find("[data-schema-path='$.test'] input");
        input.Input("ab");
        rendered.WaitForAssertion(() => Assert.IsFalse(validation?.IsValid));

        input.Input(string.Empty);
        rendered.WaitForAssertion(() => Assert.IsTrue(validation?.IsValid));
        Assert.IsFalse(value.TryGetProperty("test", out _));

        rendered.Find("[data-testid='schema-raw-mode']").Click();
        Assert.DoesNotContain("\"test\"", rendered.Find("textarea").GetAttribute("value") ?? string.Empty, StringComparison.Ordinal);
    }

    [TestMethod]
    public void EmptyNewValueAppliesDefaultsButExamplesRemainGuidance()
    {
        using var context = Context();
        JsonElement value = default;
        var rendered = context.Render<JsonSchemaInputEditor>(parameters => parameters
            .Add(component => component.Schema, Json("""{"type":"object","properties":{"count":{"type":"integer","default":3},"query":{"type":"string","examples":["docs"]}}}"""))
            .Add(component => component.Value, Json("{}"))
            .Add(component => component.ValueChanged, next => value = next));

        rendered.WaitForAssertion(() => Assert.AreEqual(3, value.GetProperty("count").GetInt32()));
        Assert.IsFalse(value.TryGetProperty("query", out _));
        StringAssert.Contains(rendered.Markup, "docs");
    }

    [TestMethod]
    public void LockedTopLevelValueIsVisibleButCannotBeChanged()
    {
        using var context = Context();
        JsonElement value = default;
        IReadOnlyDictionary<string, JsonElement> lockedValues = new Dictionary<string, JsonElement>
        {
            ["dryRun"] = Json("true")
        };
        var rendered = context.Render<JsonSchemaInputEditor>(parameters => parameters
            .Add(component => component.Schema, Json("""{"type":"object","properties":{"dryRun":{"type":"boolean"},"prompt":{"type":"string"}},"additionalProperties":false}"""))
            .Add(component => component.Value, Json("{}"))
            .Add(component => component.LockedValues, lockedValues)
            .Add(component => component.ValueChanged, next => value = next));

        var dryRun = rendered.Find("[data-schema-path='$.dryRun'] input");
        Assert.IsTrue(dryRun.HasAttribute("checked"));
        Assert.IsTrue(dryRun.HasAttribute("disabled"));
        StringAssert.Contains(rendered.Find("[data-schema-path='$.dryRun']").ClassName, "schema-field-locked");
        Assert.IsTrue(value.GetProperty("dryRun").GetBoolean());

        rendered.Find("[data-testid='schema-raw-mode']").Click();
        Assert.DoesNotContain("dryRun", rendered.Find("textarea").GetAttribute("value") ?? string.Empty, StringComparison.Ordinal);
    }

    [TestMethod]
    public void NestedObjectsAndBoundedArraysRemainEditable()
    {
        using var context = Context();
        JsonElement value = default;
        JsonSchemaInputValidationState? validation = null;
        var rendered = context.Render<JsonSchemaInputEditor>(parameters => parameters
            .Add(component => component.Schema, Json("""{"type":"object","properties":{"options":{"type":"object","properties":{"enabled":{"type":"boolean"}},"required":["enabled"]},"tags":{"type":"array","items":{"type":"string"},"minItems":1,"maxItems":2}},"required":["options","tags"]}"""))
            .Add(component => component.Value, Json("""{"options":{"enabled":false},"tags":[]}"""))
            .Add(component => component.ValueChanged, next => value = next)
            .Add(component => component.ValidationChanged, next => validation = next));

        rendered.WaitForAssertion(() => Assert.IsFalse(validation?.IsValid));
        rendered.FindAll("button").Single(button => button.TextContent.Contains("Ajouter", StringComparison.Ordinal) || button.TextContent.Contains("Add", StringComparison.Ordinal)).Click();
        rendered.Find("[data-schema-path='$.tags[0]'] input").Input("docs");
        rendered.WaitForAssertion(() => Assert.IsTrue(validation?.IsValid));
        Assert.AreEqual("docs", value.GetProperty("tags")[0].GetString());
        Assert.IsFalse(value.GetProperty("options").GetProperty("enabled").GetBoolean());
    }

    private static BunitContext Context()
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        return context;
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}
