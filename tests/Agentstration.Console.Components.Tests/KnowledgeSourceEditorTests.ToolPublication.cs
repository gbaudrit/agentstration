using Agentstration.Resources;
using Agentstration.Web.Components.Pages;
using Agentstration.Web.Components.State;
using Agentstration.Web.Console;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

public sealed partial class KnowledgeSourceEditorTests
{
    [TestMethod]
    [DataRow("en-US", "Publish a new Tool version", "Choose a different version")]
    [DataRow("fr-FR", "Publier une nouvelle version des Tools", "Choisissez une autre version")]
    public void ExistingKnowledgeToolsCanBeRepublishedWithoutLosingThePublishedExposure(string locale, string label, string warning)
    {
        using var culture = new TestCultureScope(locale);
        var source = ProjectedSource();
        var client = new KnowledgeClientStub(source, exposure: ExistingExposure());
        using var context = CreateContext(client);
        var rendered = context.Render<KnowledgeSourceDetails>(parameters => parameters
            .Add(value => value.Name, source.Name));
        rendered.WaitForElement("[data-testid=knowledge-source-tools-tab]").Click();
        rendered.WaitForAssertion(() => Assert.AreEqual(label, rendered.Find("[data-testid=knowledge-publish-tools]").TextContent));
        Assert.AreEqual("1.0.1", rendered.Find("[data-testid=knowledge-tool-exposure-version]").GetAttribute("value"));
        rendered.Find("[data-testid=knowledge-tool-exposure-version]").Input(" ");
        Assert.IsTrue(rendered.Find("[data-testid=knowledge-publish-tools]").HasAttribute("disabled"));
        rendered.Find("[data-testid=knowledge-tool-exposure-version]").Input("1.0.0");
        Assert.IsTrue(rendered.Find("[data-testid=knowledge-publish-tools]").HasAttribute("disabled"));
        Assert.Contains(warning, rendered.Markup);
        rendered.Find("[data-testid=knowledge-tool-exposure-version]").Input(" 1.0.1 ");
        rendered.Find("[data-testid=knowledge-source-approval-option] input").Change(true);
        rendered.Find("[data-testid=knowledge-publish-tools]").Click();
        rendered.WaitForAssertion(() => Assert.AreEqual("1.0.1", client.PublishedExposureRequest?.Version));
        Assert.IsTrue(client.PublishedExposureRequest!.RequiresApproval);
        Assert.Contains("1.0.1", rendered.Find("[data-testid=knowledge-tool-exposure-current-version]").TextContent);
        Assert.AreEqual("1.0.2", rendered.Find("[data-testid=knowledge-tool-exposure-version]").GetAttribute("value"));
        Assert.HasCount(1, rendered.FindAll("[data-testid=knowledge-tool-link]"));

        client.RejectExposurePublication = true;
        rendered.Find("[data-testid=knowledge-publish-tools]").Click();
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll(".form-alert-danger")));
        Assert.Contains("1.0.1", rendered.Find("[data-testid=knowledge-tool-exposure-current-version]").TextContent);
        Assert.AreEqual("1.0.2", rendered.Find("[data-testid=knowledge-tool-exposure-version]").GetAttribute("value"));
        Assert.IsFalse(rendered.Find("[data-testid=knowledge-publish-tools]").HasAttribute("disabled"));
    }

    [TestMethod]
    public void InheritedKnowledgeToolsStayVisibleWithoutPublicationControls()
    {
        using var context = CreateContext(new KnowledgeClientStub(ProjectedSource() with { ScopeRef = ResourceScopeRef.Instance }, exposure: ExistingExposure()));
        context.Services.AddSingleton<IConsoleNavigationContext>(new KnowledgeNavigationContext(DataSourceScope.Value));
        var rendered = context.Render<KnowledgeSourceDetails>(parameters => parameters
            .Add(value => value.Name, "agentstration-documentation"));
        rendered.WaitForElement("[data-testid=knowledge-source-tools-tab]").Click();
        rendered.WaitForAssertion(() => Assert.HasCount(1, rendered.FindAll("[data-testid=knowledge-tool-link]")));
        Assert.IsEmpty(rendered.FindAll("[data-testid=knowledge-publish-tools]"));
        Assert.IsEmpty(rendered.FindAll("[data-testid=knowledge-tool-exposure-version]"));
    }
}
