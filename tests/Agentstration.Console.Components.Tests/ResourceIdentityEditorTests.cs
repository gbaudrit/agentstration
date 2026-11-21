using System.Linq.Expressions;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class ResourceIdentityEditorTests
{
    [TestMethod]
    [DataRow("en-US", "Workspace · Project workspace")]
    [DataRow("fr-FR", "Espace de travail · Project workspace")]
    public void SavedOwnershipIsAReadOnlyFieldAndCreationRetainsItsScopePicker(string culture, string label)
    {
        using var locale = new TestCultureScope(culture);
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        var model = new AgentEditorModel { Name = "example", DisplayName = "Example" };
        var scope = ResourceScopeRef.Workspace(Guid.NewGuid());
        ResourceScopeRef? selected = scope;
        var targets = new[] { new ResourceScopeTargetResponse(scope, ResourceScopeKind.Workspace, "Project workspace", true) };
        var page = context.Render<CascadingValue<EditContext>>(p => p.Add(c => c.Value, new EditContext(model))
            .AddChildContent<ResourceIdentityEditor>(identity => identity
                .Add(c => c.ResourceKind, "Agent").Add(c => c.DisplayName, model.DisplayName).Add(c => c.DisplayNameExpression, () => model.DisplayName)
                .Add(c => c.TechnicalName, model.Name).Add(c => c.TechnicalNameExpression, () => model.Name)
                .Add(c => c.Namespace, model.Namespace).Add(c => c.NamespaceExpression, () => model.Namespace)
                .Add(c => c.DescriptionExpression, (Expression<Func<string?>>)(() => model.Description))
                .Add(c => c.LabelsExpression, () => model.Tags).Add(c => c.ScopeRef, scope)
                .Add(c => c.ScopeTargets, targets).Add(c => c.ScopeRefChanged, (ResourceScopeRef? value) => { selected = value; })));
        var field = page.Find("[data-testid=resource-scope-readonly]");
        Assert.IsTrue(field.HasAttribute("readonly"));
        Assert.IsFalse(field.HasAttribute("disabled"));
        Assert.AreEqual(label, field.GetAttribute("value"));
        Assert.IsEmpty(page.FindComponents<ResourceScopeFormField>());
        Assert.IsEmpty(page.FindAll(".resource-scope-badge"));
        Assert.AreEqual(scope, selected);

        var editor = page.FindComponent<ResourceIdentityEditor>();
        editor.Render(p => p.Add(c => c.IsNew, true));
        Assert.IsEmpty(page.FindAll("[data-testid=resource-scope-readonly]"));
        Assert.AreEqual(scope.Value, page.Find("[data-testid=resource-scope-picker]").GetAttribute("value"));
        editor.Render(p => p.Add(c => c.ScopeReadOnly, true));
        Assert.IsEmpty(page.FindAll("select"));
        Assert.AreEqual(label, page.Find("[data-testid=resource-scope-readonly]").GetAttribute("value"));
        Assert.AreEqual(scope, selected);
    }

    [TestMethod]
    public void AdditionalPanelsFollowTheCommonPanelAndShareItsEditContext()
    {
        using var context = new BunitContext();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.ComponentFactories.AddStub<ResourceScopeFormField>();
        var model = new AgentEditorModel { Name = "example", DisplayName = "Example", Tags = "environment=local" };
        var editContext = new EditContext(model);
        RenderFragment additional = builder =>
        {
            builder.OpenComponent<FormSection>(0);
            builder.AddAttribute(1, nameof(FormSection.Title), "Additional identity panel");
            builder.AddAttribute(2, nameof(FormSection.TestId), "extra-panel");
            builder.AddAttribute(3, nameof(FormSection.ChildContent), (RenderFragment)(child => { child.OpenComponent<ContextProbe>(0); child.CloseComponent(); }));
            builder.CloseComponent();
        };
        var cut = context.Render<CascadingValue<EditContext>>(p => p.Add(c => c.Value, editContext)
            .AddChildContent<ResourceIdentityEditor>(identity => identity
                .Add(c => c.ResourceKind, "Agent").Add(c => c.DisplayName, model.DisplayName).Add(c => c.DisplayNameExpression, () => model.DisplayName)
                .Add(c => c.TechnicalName, model.Name).Add(c => c.TechnicalNameExpression, () => model.Name)
                .Add(c => c.Namespace, model.Namespace).Add(c => c.NamespaceExpression, () => model.Namespace)
                .Add(c => c.ScopeRef, ResourceScopeRef.Instance)
                .Add(c => c.DescriptionExpression, (Expression<Func<string?>>)(() => model.Description))
                .Add(c => c.Labels, model.Tags).Add(c => c.LabelsExpression, () => model.Tags)
                .Add(c => c.ReadOnly, true).Add(c => c.TestId, "common-panel").Add(c => c.AdditionalPanels, additional)));
        var common = cut.Find("[data-testid=common-panel]");
        Assert.AreEqual("extra-panel", common.NextElementSibling!.GetAttribute("data-testid"));
        Assert.IsNotNull(common.QuerySelector("[data-testid=labels-editor]"));
        Assert.AreSame(editContext, cut.FindComponent<ContextProbe>().Instance.Context);
        Assert.IsTrue(common.QuerySelector("fieldset")!.HasAttribute("disabled"));
        Assert.IsTrue(cut.FindComponent<LabelsEditor>().Instance.ReadOnly);
        Assert.IsEmpty(cut.FindAll("[data-testid=label-add]"));
    }

    [TestMethod]
    public void AFamilyWithoutAMetadataWriteContractCanKeepSavedLabelsReadOnlyWhileEditingIdentity()
    {
        using var context = new BunitContext(); context.JSInterop.Mode = JSRuntimeMode.Loose;
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.ComponentFactories.AddStub<ResourceScopeFormField>();
        var model = new ParameterEditorModel { Name = "value", DisplayName = "Value", Tags = "environment=local" };
        var cut = context.Render<CascadingValue<EditContext>>(p => p.Add(c => c.Value, new EditContext(model))
            .AddChildContent<ResourceIdentityEditor>(identity => identity
                .Add(c => c.ResourceKind, "Parameter").Add(c => c.DisplayName, model.DisplayName).Add(c => c.DisplayNameExpression, () => model.DisplayName)
                .Add(c => c.TechnicalName, model.Name).Add(c => c.TechnicalNameExpression, () => model.Name)
                .Add(c => c.Namespace, model.Namespace).Add(c => c.NamespaceExpression, () => model.Namespace)
                .Add(c => c.DescriptionExpression, (Expression<Func<string?>>)(() => model.Description))
                .Add(c => c.Labels, model.Tags).Add(c => c.LabelsExpression, () => model.Tags).Add(c => c.LabelsReadOnly, true)));
        Assert.IsTrue(cut.FindComponent<LabelsEditor>().Instance.ReadOnly);
        Assert.IsEmpty(cut.FindAll("[data-testid=label-add]"));
        Assert.IsFalse(cut.FindComponent<ResourceIdentityEditor>().Instance.ReadOnly);
        Assert.IsFalse(cut.Find("fieldset").HasAttribute("disabled"));
    }

    public sealed class ContextProbe : ComponentBase
    {
        [CascadingParameter] public EditContext Context { get; set; } = default!;
    }
}
