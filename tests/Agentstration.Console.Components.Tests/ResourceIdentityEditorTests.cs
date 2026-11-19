using System.Linq.Expressions;
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

    public sealed class ContextProbe : ComponentBase
    {
        [CascadingParameter] public EditContext Context { get; set; } = default!;
    }
}
