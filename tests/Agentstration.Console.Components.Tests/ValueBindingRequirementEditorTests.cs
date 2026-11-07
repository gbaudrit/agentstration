using System.Text.Json;
using Agentstration.Extensions.Contracts;
using Agentstration.Parameters;
using Agentstration.Resources;
using Agentstration.Web.Components.Values;
using Agentstration.Web.Console;
using AngleSharp.Html.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class ModelProviderNavigationTests
{
    [TestMethod]
    public void ParameterAllowedValuesCanFinishRenderingAfterTheDialogCloses()
    {
        using var context = CreateContext(out _);
        var rendered = context.Render<ContextualParameterCreator>(parameters => parameters
            .Add(value => value.IsOpen, true)
            .Add(value => value.Context, new(WorkspaceScope, "authentication", "Authentication",
                AllowedValues: [JsonSerializer.SerializeToElement("ApiKey"), JsonSerializer.SerializeToElement("ManagedIdentity")])));
        var options = rendered.FindComponent<InputSelect<string>>().Instance.ChildContent!;

        rendered.Render(parameters => parameters
            .Add(value => value.IsOpen, false)
            .Add(value => value.Context, null));
        var lateRender = context.Render(options);

        StringAssert.Contains(lateRender.Markup, "ApiKey");
        StringAssert.Contains(lateRender.Markup, "ManagedIdentity");
    }

    [TestMethod]
    [DataRow("en-US", "Required", "Literal", "Expected type")]
    [DataRow("fr-FR", "Obligatoire", "Valeur directe", "Type attendu")]
    public void SharedBindingEditorLocalizesRequirementAndSources(string cultureName, string required, string literal, string type)
    {
        using var culture = new TestCultureScope(cultureName);
        using var context = CreateContext(out _);
        var rendered = context.Render<ValueBindingRequirementEditor>(parameters => parameters
            .Add(value => value.Requirement, new("retry", true, "integer", Description: "Retry count", Format: "int64"))
            .Add(value => value.AllowedSources, [ValueBindingSourceKind.Parameter, ValueBindingSourceKind.Secret, ValueBindingSourceKind.Literal]));

        StringAssert.Contains(rendered.Markup, required);
        StringAssert.Contains(rendered.Markup, literal);
        StringAssert.Contains(rendered.Markup, type);
        StringAssert.Contains(rendered.Markup, "Retry count");
        StringAssert.Contains(rendered.Markup, "int64");
    }

    [TestMethod]
    public void SharedBindingSelectionPreservesExactScopeNamespaceAndName()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext(out _);
        var first = ScopedResourceAddress.Create(ResourceScopeRef.Instance, new("shared"), "Parameter", "endpoint");
        var second = ScopedResourceAddress.Create(WorkspaceScope, new("shared"), "Parameter", "endpoint");
        ValueBindingSelection? selected = null;
        var rendered = context.Render<ValueBindingRequirementEditor>(parameters => parameters
            .Add(value => value.Requirement, new("endpoint", true, "string"))
            .Add(value => value.AllowedSources, [ValueBindingSourceKind.Parameter])
            .Add(value => value.Options, [new(ValueBindingSourceKind.Parameter, first, "Instance"), new(ValueBindingSourceKind.Parameter, second, "Workspace")])
            .Add(value => value.SelectionChanged, value => selected = value));

        rendered.Find("select[data-testid$='binding-target']").Change($"{second.ScopeRef}|shared|endpoint");

        Assert.AreEqual(second, selected?.Resource);
        Assert.AreEqual(ValueBindingSourceKind.Parameter, selected?.Kind);
        Assert.IsFalse(rendered.Markup.Contains("needs a binding", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SharedBindingPreservesUnavailableReferenceAndRejectsUnlistedTargets()
    {
        using var culture = new TestCultureScope("en-US");
        using var context = CreateContext(out _);
        var missing = ScopedResourceAddress.Create(WorkspaceScope, new("shared"), "Parameter", "removed");
        var changes = 0;
        var rendered = context.Render<ValueBindingRequirementEditor>(parameters => parameters
            .Add(value => value.Requirement, new("endpoint", true, "string"))
            .Add(value => value.AllowedSources, [ValueBindingSourceKind.Parameter])
            .Add(value => value.Selection, new(ValueBindingSourceKind.Parameter, missing))
            .Add(value => value.SelectionChanged, _ => changes++));

        var target = rendered.Find("select[data-testid$='binding-target']");
        Assert.AreEqual($"{WorkspaceScope}|shared|removed", ((IHtmlSelectElement)target).Value);
        StringAssert.Contains(target.TextContent, "Unavailable");
        Assert.IsTrue(target.QuerySelector("option[selected]")!.HasAttribute("disabled"));
        target.Change("/instance|default|unlisted");
        Assert.AreEqual(0, changes);
    }

    [TestMethod]
    public void SharedSecuredRequirementOnlyOffersSecretsEvenWhenConsumerAllowsLiteral()
    {
        using var context = CreateContext(out _);
        var changes = 0;
        var rendered = context.Render<ValueBindingRequirementEditor>(parameters => parameters
            .Add(value => value.Requirement, new("credential", true, "string", Secured: true))
            .Add(value => value.AllowedSources, [ValueBindingSourceKind.Parameter, ValueBindingSourceKind.Literal, ValueBindingSourceKind.Secret])
            .Add(value => value.Selection, new(ValueBindingSourceKind.Secret))
            .Add(value => value.SelectionChanged, _ => changes++));

        var source = rendered.Find("select[data-testid$='binding-kind']");
        CollectionAssert.AreEqual(new[] { "Secret" }, source.QuerySelectorAll("option").Select(value => value.GetAttribute("value")).ToArray());
        source.Change("Literal");
        source.Change("Parameter");
        Assert.AreEqual(0, changes);
        Assert.HasCount(0, rendered.FindAll("[data-testid$='binding-literal']"));
    }

    [TestMethod]
    [DataRow("string", "https://endpoint.test", JsonValueKind.String)]
    [DataRow("integer", "12", JsonValueKind.Number)]
    [DataRow("number", "1.25", JsonValueKind.Number)]
    [DataRow("boolean", "false", JsonValueKind.False)]
    public void SharedLiteralEmitsTypedValueWithoutCreatingAResource(string type, string text, JsonValueKind kind)
    {
        using var context = CreateContext(out _);
        ValueBindingSelection? selected = null;
        var rendered = context.Render<ValueBindingRequirementEditor>(parameters => parameters
            .Add(value => value.Requirement, new("setting", true, type))
            .Add(value => value.AllowedSources, [ValueBindingSourceKind.Literal])
            .Add(value => value.Selection, new(ValueBindingSourceKind.Literal))
            .Add(value => value.SelectionChanged, value => selected = value));

        rendered.Find("[data-testid$='binding-literal']").Change(text);

        Assert.IsNotNull(selected);
        Assert.IsTrue(selected.IsConfigured);
        Assert.IsNull(selected.Resource);
        Assert.AreEqual(kind, selected.Literal?.ValueKind);
        Assert.HasCount(0, rendered.FindAll("[data-testid$='binding-create']"));
        Assert.HasCount(0, rendered.FindAll("[role='alert']"));
    }

    [TestMethod]
    public void SharedLiteralRejectsWrongTypesAndDisallowedValuesAndClearsWhenSourceChanges()
    {
        using var context = CreateContext(out _);
        ValueBindingSelection? selected = null;
        var rendered = context.Render<ValueBindingRequirementEditor>(parameters => parameters
            .Add(value => value.Requirement, new("retry", true, "integer", AllowedValues: [JsonSerializer.SerializeToElement(3), JsonSerializer.SerializeToElement(5)]))
            .Add(value => value.AllowedSources, [ValueBindingSourceKind.Literal, ValueBindingSourceKind.Parameter])
            .Add(value => value.Selection, new(ValueBindingSourceKind.Literal))
            .Add(value => value.SelectionChanged, value => selected = value));

        var input = rendered.Find("[data-testid$='binding-literal']");
        input.Change("3.5");
        Assert.IsFalse(selected!.IsConfigured);
        Assert.IsNotNull(rendered.Find("[role='alert']"));
        input.Change("7");
        Assert.IsFalse(selected.IsConfigured);
        input.Change("5");
        Assert.AreEqual(5, selected.Literal?.GetInt32());
        rendered.Find("[data-testid$='binding-kind']").Change("Parameter");
        Assert.IsNull(selected.Literal);
        Assert.IsNull(selected.Resource);
    }

    [TestMethod]
    [DataRow("string", ParameterValueType.Text, "text")]
    [DataRow("integer", ParameterValueType.WholeNumber, "7")]
    [DataRow("number", ParameterValueType.DecimalNumber, "1.5")]
    [DataRow("boolean", ParameterValueType.Logical, "true")]
    public void SharedEditorCreatesTypedParameterInConsumerScope(string type, ParameterValueType expectedType, string text)
    {
        using var context = CreateContext(out _);
        ValueBindingSelection? selected = null;
        var rendered = context.Render<ValueBindingRequirementEditor>(parameters => parameters
            .Add(value => value.Requirement, new("setting", true, type))
            .Add(value => value.AllowedSources, [ValueBindingSourceKind.Parameter])
            .Add(value => value.CreationContext, new(WorkspaceScope, "setting", "Setting"))
            .Add(value => value.SelectionChanged, value => selected = value));

        rendered.Find("[data-testid$='binding-create']").Click();
        var typeInput = rendered.Find("[data-testid='contextual-parameter-value-type']");
        Assert.AreEqual(expectedType.ToString(), typeInput.GetAttribute("value"));
        Assert.IsTrue(typeInput.HasAttribute("disabled"));
        rendered.Find("[data-testid='contextual-parameter-value']").Change(text);
        rendered.Find("[data-testid='contextual-parameter-create']").Click();

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual(WorkspaceScope, selected?.Resource?.ScopeRef);
            Assert.AreEqual("setting", selected?.Resource?.Name);
            Assert.AreEqual(ResourceNamespace.Default, selected?.Resource?.Namespace);
            Assert.AreEqual("Parameter", selected?.Resource?.Kind);
            Assert.HasCount(0, rendered.FindAll("[role='dialog']"));
        });
    }

    [TestMethod]
    public void SharedEditorCreatesWriteOnlySecretAndReturnsOnlyExactReference()
    {
        using var context = CreateContext(out _);
        ValueBindingSelection? selected = null;
        var rendered = context.Render<ValueBindingRequirementEditor>(parameters => parameters
            .Add(value => value.Requirement, new("credential", false, "string", Secured: true))
            .Add(value => value.AllowedSources, [ValueBindingSourceKind.Secret])
            .Add(value => value.Selection, new(ValueBindingSourceKind.Secret))
            .Add(value => value.CreationContext, new(WorkspaceScope, "credential", "Credential"))
            .Add(value => value.SelectionChanged, value => selected = value));

        rendered.Find("[data-testid$='binding-create']").Click();
        rendered.WaitForElement("[data-testid='contextual-secret-value']").Change("fixture-secret-never-returned");
        rendered.Find("[data-testid='contextual-secret-create']").Click();

        rendered.WaitForAssertion(() =>
        {
            Assert.AreEqual("credential", selected?.Resource?.Name);
            Assert.AreEqual(WorkspaceScope, selected?.Resource?.ScopeRef);
            Assert.IsNull(selected?.Literal);
            Assert.IsFalse(rendered.Markup.Contains("fixture-secret-never-returned", StringComparison.Ordinal));
            Assert.HasCount(0, rendered.FindAll("[role='dialog']"));
        });
    }

    [TestMethod]
    public void SharedRequiredLiteralParticipatesInParentFormValidation()
    {
        using var context = CreateContext(out _);
        var editContext = new EditContext(new object());
        var rendered = context.Render<CascadingValue<EditContext>>(parameters => parameters
            .Add(value => value.Value, editContext)
            .AddChildContent<ValueBindingRequirementEditor>(editor => editor
                .Add(value => value.Requirement, new("retry", true, "integer"))
                .Add(value => value.AllowedSources, [ValueBindingSourceKind.Literal])
                .Add(value => value.Selection, new(ValueBindingSourceKind.Literal))));

        Assert.IsFalse(editContext.Validate());
        rendered.Find("[data-testid$='binding-literal']").Change("not-an-integer");
        Assert.IsFalse(editContext.Validate());
        rendered.Find("[data-testid$='binding-literal']").Change("5");
        Assert.IsTrue(editContext.Validate());
    }

    [TestMethod]
    public void ModelProviderSavesLiteralFromSharedEditorWithoutAParameterOrSecret()
    {
        ExtensionResponse extension = new(
            RegistrationName: "typed-extension", RegistrationNamespace: ResourceNamespace.DefaultValue,
            Endpoint: new Uri("http://localhost:5000"), Status: "available",
            Extension: new("typed.extension", "Typed extension", "1.0.0", null),
            Contributions: [new("model-provider", "typed")],
            OptionSets: [], Usages: [], Providers: [], Details: null, DiscoverySource: "manual",
            ValueRequirements: [new("model-provider", "typed", "retry", true, "integer", "standard", "Retries")]);
        using var context = CreateContext(out var providers, [extension]);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(
            "/modelproviders/new?extension=typed-extension&extensionNamespace=default&contributionId=typed");
        var rendered = context.Render<Agentstration.Web.Components.Pages.ModelProviderDetails>();
        rendered.WaitForElement("[data-testid='model-provider-name']").Change("literal-provider");
        rendered.Find("[data-testid='model-provider-display-name']").Change("Literal provider");
        rendered.Find("[data-testid='model-provider-binding-kind']").Change("Literal");
        rendered.Find("[data-testid='model-provider-binding-literal']").Change("7");
        rendered.Find("form[data-testid='model-provider-form']").Submit();

        rendered.WaitForAssertion(() =>
        {
            Assert.IsNotNull(providers.CreatedRequest);
            var binding = providers.CreatedRequest.Properties.ValueBindings.Single();
            Assert.AreEqual(7, binding.Literal?.GetInt32());
            Assert.IsNull(binding.Parameter);
            Assert.IsNull(binding.Secret);
        });
    }

    [TestMethod]
    public async Task SharedCreatorRejectsAnAddressFromAnotherScopeAndClosesWhenScopeChanges()
    {
        using var context = CreateContext(out _);
        var changes = 0;
        var rendered = context.Render<ValueBindingRequirementEditor>(parameters => parameters
            .Add(value => value.Requirement, new("setting", false, "string"))
            .Add(value => value.AllowedSources, [ValueBindingSourceKind.Parameter])
            .Add(value => value.CreationContext, new(WorkspaceScope, "setting", "Setting"))
            .Add(value => value.SelectionChanged, _ => changes++));
        rendered.Find("[data-testid$='binding-create']").Click();
        var creator = rendered.FindComponent<ContextualParameterCreator>();
        await rendered.InvokeAsync(() => creator.Instance.OnCreated.InvokeAsync(
            ScopedResourceAddress.Create(ResourceScopeRef.Instance, ResourceNamespace.Default, "Parameter", "setting")));
        Assert.AreEqual(0, changes);
        rendered.Render(parameters => parameters.Add(value => value.CreationContext, new(ResourceScopeRef.Instance, "setting", "Setting")));
        Assert.HasCount(0, rendered.FindAll("[role='dialog']"));
    }

    [TestMethod]
    public void SharedCreatorClosesWhenTheConsumerBecomesReadOnly()
    {
        using var context = CreateContext(out _);
        var rendered = context.Render<ValueBindingRequirementEditor>(parameters => parameters
            .Add(value => value.Requirement, new("setting", false, "string"))
            .Add(value => value.AllowedSources, [ValueBindingSourceKind.Parameter])
            .Add(value => value.CreationContext, new(WorkspaceScope, "setting", "Setting")));
        rendered.Find("[data-testid$='binding-create']").Click();
        Assert.IsNotNull(rendered.Find("[role='dialog']"));
        rendered.Render(parameters => parameters.Add(value => value.Disabled, true));
        Assert.HasCount(0, rendered.FindAll("[role='dialog']"));
        Assert.IsTrue(rendered.Find("[data-testid$='binding-create']").HasAttribute("disabled"));
    }

    [TestMethod]
    public void SharedLiteralRevalidatesWhenRequirementMetadataChanges()
    {
        using var context = CreateContext(out _);
        var rendered = context.Render<ValueBindingRequirementEditor>(parameters => parameters
            .Add(value => value.Requirement, new("setting", true, "integer"))
            .Add(value => value.AllowedSources, [ValueBindingSourceKind.Literal])
            .Add(value => value.Selection, new(ValueBindingSourceKind.Literal, Literal: JsonSerializer.SerializeToElement(3))));
        Assert.HasCount(0, rendered.FindAll("[role='alert']"));
        rendered.Render(parameters => parameters.Add(value => value.Requirement, new("setting", true, "string")));
        Assert.IsNotNull(rendered.Find("[role='alert']"));
        rendered.Render(parameters => parameters.Add(value => value.Requirement,
            new("setting", true, "integer", AllowedValues: [JsonSerializer.SerializeToElement(5)])));
        Assert.IsNotNull(rendered.Find("[role='alert']"));
        rendered.Render(parameters => parameters.Add(value => value.Requirement,
            new("setting", true, "integer", AllowedValues: [JsonSerializer.SerializeToElement(3)])));
        Assert.HasCount(0, rendered.FindAll("[role='alert']"));
    }
}
