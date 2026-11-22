using System.Text.Json;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.Values;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class ConnectedServiceComponentTests
{
    [TestMethod]
    [DataRow("en-US", "Expose all discovered operations using the service access rules", "Yes")]
    [DataRow("fr-FR", "Exposer toutes les opérations découvertes selon les règles d’accès du service", "Oui")]
    public void ServiceWizardLocalizesAndReviewsCollectiveOperationExposure(string locale, string label, string yes)
    {
        using var culture = new TestCultureScope(locale);
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var scope = ResourceScopeRef.Workspace(Guid.NewGuid());
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConnectedServicesClient>(new EditorClient(TypeCatalog(), scope));
        context.Services.AddSingleton<IParametersClient>(new ParametersClientStub([]));
        context.Services.AddSingleton<ISecretsClient>(new SecretsClientStub([]));
        var cut = context.Render<ConnectedServiceEditor>();
        cut.Find("[data-wizard-step=access]").Click();
        var checkbox = cut.Find("[data-testid=connected-service-expose-operations]");
        Assert.AreEqual("switch", checkbox.GetAttribute("role"));
        Assert.IsTrue(checkbox.HasAttribute("checked"));
        StringAssert.Contains(checkbox.ParentElement!.TextContent, label);
        checkbox.Change(false);
        checkbox.Change(true);
        Assert.IsTrue(((ConnectedServiceEditorModel)cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext.Model).ExposeOperationsToDescendants);
        cut.Find("[data-wizard-step=review]").Click();
        Assert.AreEqual(yes, cut.Find("[data-testid=connected-service-operation-exposure-review]").TextContent);
    }

    [TestMethod]
    [DataRow("en-US", "Display name")]
    [DataRow("fr-FR", "Nom d’affichage")]
    public void ServiceWizardUsesCommonIdentityFormAndRootBindingValidation(string locale, string displayLabel)
    {
        using var culture = new TestCultureScope(locale);
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var scope = ResourceScopeRef.Workspace(Guid.NewGuid());
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConnectedServicesClient>(new EditorClient(TypeCatalog(), scope));
        context.Services.AddSingleton<IParametersClient>(new ParametersClientStub([]));
        context.Services.AddSingleton<ISecretsClient>(new SecretsClientStub([]));
        var cut = context.Render<ConnectedServiceEditor>();
        var common = cut.FindComponent<ResourceDefinitionForm>().Instance;
        Assert.IsTrue(cut.FindComponent<ResourceIdentityEditor>().Instance.LabelsReadOnly);
        Assert.HasCount(1, cut.FindAll("form"));
        foreach (var binding in cut.FindComponents<ValueBindingRequirementEditor>())
        {
            Assert.IsNotNull(binding.Instance.ValidationField);
            Assert.AreSame(common.EditContext.Model, binding.Instance.ValidationField.Value.Model);
            Assert.AreEqual("Bindings", binding.Instance.ValidationField.Value.FieldName);
        }
        cut.Find("[data-wizard-step=review]").Click();
        cut.Find("[data-testid=connected-service-save]").Click();
        StringAssert.Contains(string.Join(" ", common.EditContext.GetValidationMessages()), displayLabel);
        cut.Find("[data-testid=connected-service-display-name]").Change("Service");
        cut.Find("[data-testid=connected-service-name]").Change("service");
        cut.Find("[data-wizard-step=review]").Click();
        cut.Find("[data-testid=connected-service-save]").Click();
        Assert.AreEqual("step", cut.Find("[data-wizard-step=configuration]").GetAttribute("aria-current"));
        Assert.IsTrue(common.EditContext.GetValidationMessages().Any());
    }

    [TestMethod]
    public void InheritedServiceShowsSavedOperationExposureWithoutAllowingChanges()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var scope = ResourceScopeRef.Tenant(Guid.NewGuid());
        var type = TypeCatalog();
        var original = Resource(scope, type);
        original = original with { Uid = Guid.NewGuid(), Definition = original.Definition with { ExposeOperationsToDescendants = true } };
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConnectedServicesClient>(new DetailsClient(original, type, scope));
        context.Services.AddSingleton<IParametersClient>(new ParametersClientStub([]));
        context.Services.AddSingleton<ISecretsClient>(new SecretsClientStub([]));
        var cut = context.Render<ConnectedServiceEditor>(p => p.Add(v => v.Name, original.Name).Add(v => v.Embedded, true)
            .Add(v => v.EmbeddedScopeRef, scope).Add(v => v.InitialSnapshot, new ResourceSnapshot<ConnectedServiceResource>(original, "etag"))
            .Add(v => v.ReadOnlyReason, ResourceReadOnlyReason.Inherited));
        var checkbox = cut.Find("[data-testid=connected-service-expose-operations]");
        Assert.IsTrue(checkbox.HasAttribute("checked"));
        Assert.IsTrue(checkbox.HasAttribute("disabled"));
        Assert.IsFalse(cut.Instance.IsDirty);
        checkbox.Change(false);
        Assert.IsTrue(((ConnectedServiceEditorModel)cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext.Model).ExposeOperationsToDescendants);
    }

    [TestMethod]
    public async Task ServiceEditorRetainsInvalidSchemaAndOriginalVersionUntilExplicitSnapshotDiscard()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var scope = ResourceScopeRef.Workspace(Guid.NewGuid());
        var type = TypeCatalog();
        var original = Resource(scope, type) with { Uid = Guid.NewGuid() };
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConnectedServicesClient>(new DetailsClient(original, type, scope));
        context.Services.AddSingleton<IParametersClient>(new ParametersClientStub([]));
        context.Services.AddSingleton<ISecretsClient>(new SecretsClientStub([]));
        var cut = context.Render<ConnectedServiceEditor>(p => p.Add(v => v.Name, original.Name).Add(v => v.Embedded, true)
            .Add(v => v.EmbeddedScopeRef, scope).Add(v => v.InitialSnapshot, new ResourceSnapshot<ConnectedServiceResource>(original, "old-etag")));
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance;
        cut.Find("[data-testid=schema-raw-mode]").Click();
        cut.Find(".schema-input-editor textarea").Change("{invalid");
        Assert.IsTrue(cut.Instance.IsDirty);
        var newer = new ResourceSnapshot<ConnectedServiceResource>(original with { Generation = 2 }, "new-etag");
        var accepted = false;
        await cut.InvokeAsync(() => accepted = cut.Instance.ApplySnapshot(newer));
        Assert.IsFalse(accepted);
        Assert.AreSame(form.EditContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("old-etag", cut.Instance.Version);
        Assert.AreEqual("{invalid", cut.Find(".schema-input-editor textarea").GetAttribute("value"));
        await cut.InvokeAsync(() => accepted = cut.Instance.ApplySnapshot(newer, discard: true));
        Assert.IsTrue(accepted);
        Assert.AreEqual("new-etag", cut.Instance.Version);
        Assert.AreNotSame(form.EditContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.DoesNotContain("{invalid", cut.Markup);
    }

    [TestMethod]
    public async Task ServiceEditorRejectsAReplacedActivationIdentity()
    {
        using var context = new BunitContext().WithCollectionPresentation();
        context.JSInterop.Mode = JSRuntimeMode.Loose;
        var scope = ResourceScopeRef.Workspace(Guid.NewGuid()); var type = TypeCatalog();
        var original = Resource(scope, type) with { Uid = Guid.NewGuid() };
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<IConnectedServicesClient>(new DetailsClient(original, type, scope));
        context.Services.AddSingleton<IParametersClient>(new ParametersClientStub([]));
        context.Services.AddSingleton<ISecretsClient>(new SecretsClientStub([]));
        var cut = context.Render<ConnectedServiceEditor>(p => p.Add(v => v.Name, original.Name).Add(v => v.Embedded, true)
            .Add(v => v.EmbeddedScopeRef, scope).Add(v => v.InitialSnapshot, new ResourceSnapshot<ConnectedServiceResource>(original, "old-etag")));
        await cut.InvokeAsync(() => cut.Instance.ApplyActivationSnapshot(new(original with { Uid = Guid.NewGuid(), Definition = original.Definition with { Enabled = false } }, "wrong-etag")));
        Assert.AreEqual("old-etag", cut.Instance.Version);
        Assert.IsTrue(((Agentstration.Web.Console.ConnectedServiceEditorModel)cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext.Model).Enabled);
    }
}
