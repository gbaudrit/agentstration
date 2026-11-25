using Agentstration.Web.Components.Flows;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class FlowOperationProgressSummaryTests
{
    [TestMethod]
    [DataRow("en-US", "Confirm Storage publication", "Waiting for provider; retry scheduled", "Attempt 2")]
    [DataRow("fr-FR", "Confirmer la publication Storage", "En attente du fournisseur ; nouvelle tentative prévue", "Tentative 2")]
    public void ProgressUsesLocalizedDurablePhaseAndWaitEvidenceWithoutInventingPercentages(string cultureName, string phase, string wait, string attempt)
    {
        using var culture = new TestCultureScope(cultureName);
        using var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        context.Services.AddSingleton<TimeProvider>(new ProgressClock());
        var cut = context.Render<FlowOperationProgressSummary>(parameters => parameters
            .Add(value => value.StepName, "publication-seal").Add(value => value.State, "WaitingForProvider")
            .Add(value => value.Attempt, 2).Add(value => value.ErrorCode, "storage_publication_unavailable")
            .Add(value => value.StartedAt, ProgressClock.Now.AddMinutes(-2)).Add(value => value.UpdatedAt, ProgressClock.Now.AddMinutes(-2))
            .Add(value => value.RunUrl, "/flow-runs/accepted-root"));
        var text = cut.Find("[role='status']").TextContent;
        Assert.Contains(phase, text);
        Assert.Contains(wait, text);
        Assert.Contains(attempt, text);
        Assert.DoesNotContain("%", text);
        Assert.Contains("00:02:00", text);
        Assert.AreEqual("/flow-runs/accepted-root", cut.Find("a").GetAttribute("href"));
        cut.Render(parameters => parameters.Add(value => value.State, "Succeeded").Add(value => value.CompletedAt, ProgressClock.Now));
        Assert.IsEmpty(cut.FindAll("progress"), "Terminal evidence must not render an active progress indicator.");
    }

    private sealed class ProgressClock : TimeProvider
    {
        public static DateTimeOffset Now { get; } = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
