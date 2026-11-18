using Agentstration.Knowledge.Contracts;
using Agentstration.Web.Components.Pages;
using Bunit;

namespace Agentstration.Web.Tests;

public sealed partial class KnowledgeSourceEditorTests
{
    [TestMethod]
    [DataRow("en-US", KnowledgeContentAvailability.Unknown, "Not checked")]
    [DataRow("fr-FR", KnowledgeContentAvailability.Unknown, "Non vérifiée")]
    [DataRow("en-US", KnowledgeContentAvailability.Retired, "Retired")]
    [DataRow("fr-FR", KnowledgeContentAvailability.Retired, "Retiré")]
    public void NativeStatusSeparatesPublicationStateFromContentAvailability(string cultureName, KnowledgeContentAvailability availability, string text)
    {
        using var culture = new TestCultureScope(cultureName);
        var client = new KnowledgeClientStub(ProjectedSource())
        {
            ContentStatus = new()
            {
                SourceUid = Guid.NewGuid(),
                State = "ready",
                CheckedAt = DateTimeOffset.UnixEpoch,
                ContentAvailability = availability
            }
        };
        using var context = CreateContext(client);
        var rendered = context.Render<KnowledgeContentAccess>(parameters => parameters.Add(value => value.SourceName, "knowledge"));
        rendered.Find("[data-testid=knowledge-retrieval-operation]").Change("get_status");
        rendered.Find("[data-testid=knowledge-retrieval-run]").Click();
        rendered.WaitForAssertion(() =>
        {
            Assert.Contains(text, rendered.Find("[data-testid=knowledge-content-availability]").TextContent);
            Assert.HasCount(1, rendered.FindAll("[data-testid=knowledge-content-status]"));
            Assert.Contains(cultureName == "fr-FR" ? "Publication prête" : "Publication ready",
                rendered.Find("[data-testid=knowledge-content-status]").TextContent);
        });
    }
}
