using Agentstration.Web.Components.WorkOperations;
using Agentstration.Web.Console;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Bunit;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class WorkOperationsComponentTests
{
    [TestMethod]
    public void TasksStatusQueryParameterUsesABlazorSupportedScalarType()
    {
        var statusProperty = typeof(Components.Pages.Tasks).GetProperty("Status");
        Assert.IsNotNull(statusProperty);
        Assert.AreEqual(typeof(string), statusProperty.PropertyType);
    }

    [TestMethod]
    public void StatusBadgeUsesOperationalNeedsAttentionLabel()
    {
        using var context = new BunitContext();
        var rendered = context.Render<TaskStatusBadge>(parameters => parameters.Add(value => value.Status, WorkTaskStatus.ActionRequired));
        Assert.IsTrue(rendered.Markup.Contains("Needs attention", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SummaryRendersOnlyRealCounterValues()
    {
        using var context = new BunitContext();
        var rendered = context.Render<TaskOperationsSummary>(parameters => parameters.Add(value => value.Summary, new WorkTaskOperationsCountersResponse(2, 3, 4, 5, 6)));
        CollectionAssert.AreEqual(new[] { "2", "3", "4", "5", "6" }, rendered.FindAll(".task-summary-card strong").Select(value => value.TextContent).ToArray());
    }

    [TestMethod]
    public void TableRendersApiDtoAndOperationalRelations()
    {
        using var context = new BunitContext(); var now = DateTimeOffset.UtcNow;
        var item = new WorkTaskOperationsSummary(Guid.Parse("11111111-1111-1111-1111-111111111111"), "personal", "prepare-report", Guid.Parse("22222222-2222-2222-2222-222222222222"), "Monthly report", "Executive version", WorkTaskStatus.Completed, now.AddMinutes(-2), now.AddMinutes(-2), now, now, "flowrun-1", Guid.NewGuid(), 0, 2, 2, 2, "New version generated", null);
        var rendered = context.Render<TaskOperationsTable>(parameters => parameters.Add(value => value.Items, new[] { item }));
        Assert.IsTrue(rendered.Markup.Contains("Monthly report", StringComparison.Ordinal)); Assert.IsTrue(rendered.Markup.Contains("New version generated", StringComparison.Ordinal));
        Assert.AreEqual("/tasks/11111111-1111-1111-1111-111111111111", rendered.Find("a.text-button").GetAttribute("href"));
        Assert.IsFalse(rendered.Markup.Contains("Review data access boundaries", StringComparison.Ordinal), "The removed fake Task must never be rendered.");
    }

    [TestMethod]
    public void EmptyAndUnavailableStatesAreExplicit()
    {
        using var context = new BunitContext();
        var empty = context.Render<TaskOperationsTable>(); Assert.IsTrue(empty.Markup.Contains("No tasks yet", StringComparison.Ordinal));
        var unavailable = context.Render<WorkApiUnavailableState>(); Assert.IsTrue(unavailable.Markup.Contains("Work API unavailable", StringComparison.Ordinal)); Assert.AreEqual(1, unavailable.FindAll("button").Count);
    }

    [TestMethod]
    public void RealtimeStateNeverPretendsOfflineIsLive()
    {
        using var context = new BunitContext();
        var rendered = context.Render<RealtimeStatus>(parameters => parameters.Add(value => value.State, WorkOperationsRealtimeState.Offline));
        Assert.IsTrue(rendered.Markup.Contains("Offline", StringComparison.Ordinal)); Assert.IsFalse(rendered.Markup.Contains("> Live<", StringComparison.Ordinal));
    }
}
