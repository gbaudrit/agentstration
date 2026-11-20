using Agentstration.Resources;
using Agentstration.Storage.Contracts;
using Agentstration.Web.Components;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class StorageTypeComponentTests
{
    [TestMethod]
    public async Task TypeMetadataFormSharesItsDeclarationsAndKeepsAnInvalidDraftUntilExplicitDiscard()
    {
        var client = new TypesClient();
        using var context = Setup(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(StorageTypeLinks.Action(client.Resource, "edit"));
        var state = new ResourceSectionState(); state.Select("definition/identity");
        var sections = StorageTypeEditor.DefinitionPresentations();
        var cut = context.Render<StorageTypeEditor>(p => p.Add(c => c.Name, client.Resource.Name).Add(c => c.Embedded, true)
            .Add(c => c.SectionState, state).Add(c => c.SectionPresentations, sections).Add(c => c.SectionRequested, path => { state.Select(path); }));
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance;
        Assert.AreSame(state, form.SectionState); Assert.AreSame(sections, form.Sections);
        cut.Find("[data-testid=storage-type-display-name]").Change("");
        cut.Find("form").Submit();
        Assert.IsTrue(form.EditContext.GetValidationMessages().Any());
        var newer = client.Resource with { Generation = 2, Definition = client.Resource.Definition with { DisplayName = "New saved metadata" } };
        await cut.InvokeAsync(() => cut.Instance.ApplySnapshot(new(newer, "new-etag")));
        Assert.AreSame(form.EditContext, cut.FindComponent<ResourceDefinitionForm>().Instance.EditContext);
        Assert.AreEqual("", cut.Find("[data-testid=storage-type-display-name]").GetAttribute("value"));
        Assert.IsTrue(form.EditContext.GetValidationMessages().Any());
        Assert.AreEqual("\"original\"", cut.Find("[data-testid=storage-type-form]").GetAttribute("data-resource-etag"));
        await cut.InvokeAsync(() => cut.Instance.ApplySnapshot(new(newer, "new-etag"), discard: true));
        Assert.AreEqual("New saved metadata", cut.Find("[data-testid=storage-type-display-name]").GetAttribute("value"));
        Assert.AreEqual("new-etag", cut.Find("[data-testid=storage-type-form]").GetAttribute("data-resource-etag"));
        Assert.IsNull(client.Updated);
        var fresh = newer with { Generation = 3, Definition = newer.Definition with { DisplayName = "Pristine refresh" } };
        cut.Render(p => p.Add(c => c.InitialSnapshot, new ResourceSnapshot<StorageTypeResource>(fresh, "fresh-etag")));
        Assert.AreEqual("Pristine refresh", cut.Find("[data-testid=storage-type-display-name]").GetAttribute("value"));
        Assert.AreEqual("fresh-etag", cut.Find("[data-testid=storage-type-form]").GetAttribute("data-resource-etag"));
    }

    [TestMethod]
    public void TypeCreationKeepsTheExistingEmptyNamespaceMeaningAndPropertiesOnlyMetadata()
    {
        var client = new TypesClient();
        using var context = Setup(client);
        var cut = context.Render<StorageTypeEditor>();
        cut.Find("[data-testid=storage-type-display-name]").Change("Default namespace type");
        cut.Find("[data-testid=storage-type-name]").Change("default-namespace-type");
        cut.Find("[data-testid=storage-type-namespace]").Change("");
        Assert.IsTrue(cut.FindComponent<ResourceIdentityEditor>().Instance.LabelsReadOnly);
        cut.Find("form").Submit();
        Assert.IsNotNull(client.Created);
        Assert.AreEqual(ResourceNamespace.Default, client.Created.Namespace);
        Assert.AreEqual(Scope, client.Created.ScopeRef);
        Assert.IsNull(client.Created.Properties.Versions.Single().Contribution);
    }

    [TestMethod]
    [DataRow("en-US", "Enter valid, bounded JSON schemas")]
    [DataRow("fr-FR", "Saisissez des schémas JSON valides et bornés")]
    public void PublicationReportsTheActualNestedContractFieldAndRetainsItsRawInput(string locale, string message)
    {
        using var culture = new TestCultureScope(locale);
        var client = new TypesClient();
        using var context = Setup(client);
        context.Services.GetRequiredService<NavigationManager>().NavigateTo(StorageTypeLinks.Action(client.Resource, "versions/new"));
        var cut = RenderEditor(context, client.Resource.Name);
        cut.Find("[data-testid=storage-type-version]").Change("2.0.0");
        cut.Find("[data-testid=storage-type-configuration-schema]").Change("{invalid-schema");
        cut.Find("[data-wizard-step=review]").Click();
        cut.Find("[data-testid=storage-type-save]").Click();
        Assert.AreEqual("step", cut.Find("[data-wizard-step=contract]").GetAttribute("aria-current"));
        var input = cut.Find("[data-testid=storage-type-configuration-schema]");
        Assert.AreEqual("{invalid-schema", input.GetAttribute("value"));
        StringAssert.Contains(input.Closest(".form-field")!.QuerySelector(".validation-message")!.TextContent, message);
        var form = cut.FindComponent<ResourceDefinitionForm>().Instance;
        Assert.AreEqual("definition/configuration", ResourceDefinitionForm.FirstInvalidSection(form.EditContext, form.Sections));
        Assert.IsNull(client.Published);
        Assert.AreEqual("1.0.0", client.Resource.Definition.PreferredVersion);
    }
    [TestMethod]
    public void ReturningFromPublicationToCreationDropsThePreviousResourceEvenIfTheRouteParameterIsRetained()
    {
        var client = new TypesClient();
        using var context = Setup(client);
        var navigation = context.Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(StorageTypeLinks.Action(client.Resource, "versions/new"));
        var cut = RenderEditor(context, client.Resource.Name);
        cut.Find("[data-testid=storage-type-version]").Change("2.0.0");
        navigation.NavigateTo("/storage-types/new");
        cut.Find("[role=alertdialog] .button-danger").Click();
        cut.Render(p => p.Add(c => c.Name, client.Resource.Name));
        Assert.AreEqual("", cut.Find("[data-testid=storage-type-display-name]").GetAttribute("value"));
        Assert.AreEqual(ResourceNamespace.DefaultValue, cut.Find("[data-testid=storage-type-namespace]").GetAttribute("value"));
        Assert.AreEqual("step", cut.Find("[data-wizard-step=identity]").GetAttribute("aria-current"));
        Assert.IsFalse(cut.Find("[data-testid=storage-type-name]").HasAttribute("disabled"));
        Assert.IsEmpty(cut.Find("[data-testid=storage-type-form]").GetAttribute("data-resource-etag") ?? string.Empty);
        Assert.IsNull(client.Created); Assert.IsNull(client.Published);
    }
}
