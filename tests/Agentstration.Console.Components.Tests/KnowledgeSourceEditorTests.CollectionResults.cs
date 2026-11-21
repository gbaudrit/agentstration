using Agentstration.Knowledge.Contracts;
using Agentstration.Web.Components.Pages;
using Bunit;

namespace Agentstration.Web.Tests;

public sealed partial class KnowledgeSourceEditorTests
{
    [TestMethod]
    [DataRow("en-US", "Fresh recorded input", "Missing or stale recorded input")]
    [DataRow("fr-FR", "Entrée enregistrée à jour", "Entrée enregistrée absente ou périmée")]
    public void NativeStatusListsKeepCompleteDocumentIdentityAndIndependentFreshness(string cultureName, string fresh, string stale)
    {
        using var culture = new TestCultureScope(cultureName);
        var documentId = "document/" + new string('x', 180);
        var client = new KnowledgeClientStub(ProjectedSource())
        {
            ContentStatus = new()
            {
                SourceUid = Guid.NewGuid(),
                State = "ready",
                CheckedAt = DateTimeOffset.UnixEpoch,
                Inputs = [new("fresh-input", "a", DateTimeOffset.UnixEpoch, true, false, true),
                    new("stale-input", "b", DateTimeOffset.UnixEpoch, false, true, true)],
                Documents = [new(documentId, "text/plain", 128, true)]
            }
        };
        using var context = CreateContext(client);
        var page = context.Render<KnowledgeContentAccess>(p => p.Add(c => c.SourceName, "knowledge"));
        page.Find("[data-testid=knowledge-retrieval-operation]").Change("get_status");
        page.Find("[data-testid=knowledge-retrieval-run]").Click();
        page.WaitForAssertion(() =>
        {
            var result = page.Find("[data-testid=knowledge-content-status]");
            Assert.HasCount(3, result.QuerySelectorAll(".collection-card"));
            var identity = result.QuerySelector(".copyable-value code")!;
            Assert.AreEqual(documentId, identity.TextContent);
            Assert.AreEqual(documentId, identity.GetAttribute("title"));
            Assert.HasCount(1, result.QuerySelectorAll(".status-success"));
            Assert.HasCount(1, result.QuerySelectorAll(".status-warning"));
            Assert.Contains(fresh, result.TextContent);
            Assert.Contains(stale, result.TextContent);
            Assert.HasCount(0, result.QuerySelectorAll(".resource-scope-badge, .managed-resource-badge, a"));
        });
    }
}
