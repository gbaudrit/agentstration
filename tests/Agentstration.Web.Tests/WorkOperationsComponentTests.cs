using Agentstration.Web.Components.WorkOperations;
using Agentstration.Web.Console;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using System.Globalization;

namespace Agentstration.Web.Tests;

[TestClass]
[DoNotParallelize]
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
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var rendered = context.Render<TaskStatusBadge>(parameters => parameters.Add(value => value.Status, WorkTaskStatus.ActionRequired));
        Assert.IsTrue(rendered.Markup.Contains("Needs attention", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SummaryRendersOnlyRealCounterValues()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
        var rendered = context.Render<TaskOperationsSummary>(parameters => parameters.Add(value => value.Summary, new WorkTaskOperationsCountersResponse(2, 3, 4, 5, 6)));
        CollectionAssert.AreEqual(new[] { "2", "3", "4", "5", "6" }, rendered.FindAll(".task-summary-card strong").Select(value => value.TextContent).ToArray());
    }

    [TestMethod]
    public void TableRendersApiDtoAndOperationalRelations()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext(); var now = DateTimeOffset.UtcNow;
        var item = new WorkTaskOperationsSummary(Guid.Parse("11111111-1111-1111-1111-111111111111"), "personal", "prepare-report", Guid.Parse("22222222-2222-2222-2222-222222222222"), "Monthly report", "Executive version", WorkTaskStatus.Completed, now.AddMinutes(-2), now.AddMinutes(-2), now, now, "flowrun-1", Guid.NewGuid(), 0, 2, 2, 2, "New version generated", null);
        var rendered = context.Render<TaskOperationsTable>(parameters => parameters.Add(value => value.Items, new[] { item }));
        Assert.IsTrue(rendered.Markup.Contains("Monthly report", StringComparison.Ordinal)); Assert.IsTrue(rendered.Markup.Contains("New version generated", StringComparison.Ordinal));
        Assert.AreEqual("/tasks/11111111-1111-1111-1111-111111111111", rendered.Find("a.text-button").GetAttribute("href"));
        Assert.IsFalse(rendered.Markup.Contains("Review data access boundaries", StringComparison.Ordinal), "The removed fake Task must never be rendered.");
    }

    [TestMethod]
    public void EmptyAndUnavailableStatesAreExplicit()
    {
        using var culture = new CultureScope("en-US");
        using var context = CreateContext();
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

    [TestMethod]
    public void TaskComponentsUseTheSelectedFrenchCulture()
    {
        using var culture = new CultureScope("fr-FR");
        using var context = CreateContext();

        var summary = context.Render<TaskOperationsSummary>(parameters => parameters.Add(value => value.Summary, new WorkTaskOperationsCountersResponse(2, 3, 4, 5, 6)));
        var unavailable = context.Render<WorkApiUnavailableState>();

        StringAssert.Contains(summary.Markup, "Nécessite votre attention");
        StringAssert.Contains(summary.Markup, "Terminées · 24 h");
        StringAssert.Contains(unavailable.Markup, "API Work indisponible");
    }

    [TestMethod]
    public void BusinessListCatalogsUseTheSelectedFrenchCulture()
    {
        using var culture = new CultureScope("fr-FR");
        using var context = CreateContext();
        var agents = context.Services.GetRequiredService<IStringLocalizer<Components.Pages.AgentsStrings>>();
        var agentEditor = context.Services.GetRequiredService<IStringLocalizer<Components.Pages.AgentEditorStrings>>();
        var agentRuns = context.Services.GetRequiredService<IStringLocalizer<Components.Pages.AgentRunsStrings>>();
        var agentRunnerInspector = context.Services.GetRequiredService<IStringLocalizer<Components.Pages.AgentRunnerInspectorStrings>>();
        var flows = context.Services.GetRequiredService<IStringLocalizer<Components.Pages.FlowsStrings>>();

        Assert.AreEqual("Créer un agent", agents["CreateAgent"].Value);
        Assert.AreEqual("Définissez, publiez et exploitez des ressources d’agents spécialisés.", agents["Description"].Value);
        Assert.AreEqual("Créer et déployer", agentEditor["CreateAndDeploy"].Value);
        Assert.AreEqual("Configuration déclarée et résolue", agentEditor["DeclaredAndResolved"].Value);
        Assert.AreEqual("La génération 7 est prête.", agentEditor["GenerationReady", 7].Value);
        Assert.AreEqual("Exécutions d’agents", agentRuns["Title"].Value);
        Assert.AreEqual("Aucune exécution d’agent", agentRuns["EmptyTitle"].Value);
        Assert.AreEqual("Données brutes", agentRunnerInspector["Tab.Raw"].Value);
        Assert.AreEqual("Exécutions récentes de l’agent", agentRunnerInspector["RecentAgentRuns"].Value);
        Assert.AreEqual("Copier la requête JSON", agentRunnerInspector["CopyRequestJson"].Value);
        Assert.AreEqual("Nouveau Flow", flows["NewFlow"].Value);
        Assert.AreEqual("Voir toutes les exécutions", flows["ViewAllRuns"].Value);
    }

    private static BunitContext CreateContext()
    {
        var context = new BunitContext();
        context.Services.AddLocalization(options => options.ResourcesPath = "Resources");
        return context;
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo originalCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;

        public CultureScope(string name)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
