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
    [TestMethod]
    [DataRow("en-US", "Refresh proposed Tools", "Added")]
    [DataRow("fr-FR", "Actualiser les Tools proposés", "Ajouté")]
    public void ToolProposalRefreshIsIndependentOfPublishedExposureAndPublicationCarriesReviewedDigest(string locale, string refresh, string added)
    {
        using var culture = new TestCultureScope(locale);
        var source = ProjectedSource();
        var client = new KnowledgeClientStub(source, exposure: ExistingExposure());
        var tool = new Agentstration.Knowledge.Contracts.KnowledgeToolProposal
        {
            Route = "describe",
            DefinitionName = "documentation.access.describe",
            Operation = Agentstration.Knowledge.Contracts.KnowledgeSourceOperation.Describe,
            DisplayName = "Documentation structure",
            Description = "Discover the published documentation contributions",
            InputSchema = System.Text.Json.JsonSerializer.SerializeToElement(new { type = "object" }),
            ContractDigest = "structure-contract"
        };
        client.ToolProposal = new() { SourceUid = source.Uid, SourceGeneration = source.Generation, Digest = "proposal-a", ToolSetName = source.Name + ".access", Ready = true, Tools = [tool] };
        using var context = CreateContext(client);
        var rendered = context.Render<KnowledgeSourceDetails>(p => p.Add(v => v.Name, source.Name));
        rendered.WaitForElement("[data-testid=knowledge-source-tools-tab]").Click();
        rendered.WaitForAssertion(() => Assert.AreEqual(refresh, rendered.Find("[data-testid=knowledge-refresh-tools]").TextContent));
        Assert.Contains(added, rendered.Find("[data-testid=knowledge-proposed-tool]").TextContent);
        Assert.IsNull(client.PublishedExposureRequest);
        var calls = client.ProposalCalls;
        client.ToolProposal = client.ToolProposal with { Digest = "proposal-b", Tools = [tool with { Change = "modified", Description = "Changed coverage" }] };
        rendered.Find("[data-testid=knowledge-refresh-tools]").Click();
        rendered.WaitForAssertion(() => Assert.IsGreaterThan(calls, client.ProposalCalls));
        Assert.Contains("Changed coverage", rendered.Find("[data-testid=knowledge-proposed-tool]").TextContent);
        Assert.Contains("1.0.0", rendered.Find("[data-testid=knowledge-tool-exposure-current-version]").TextContent);
        Assert.IsNull(client.PublishedExposureRequest);
        rendered.Find("[data-testid=knowledge-publish-tools]").Click();
        rendered.WaitForAssertion(() => Assert.AreEqual("proposal-b", client.PublishedExposureRequest?.ProposalDigest));
    }

}
