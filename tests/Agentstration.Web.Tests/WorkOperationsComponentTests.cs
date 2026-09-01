using System.Globalization;
using Agentstration.Web.Components.WorkOperations;
using Agentstration.Web.Console;
using Agentstration.Work;
using Agentstration.Work.Contracts;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

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
        var deployments = context.Services.GetRequiredService<IStringLocalizer<Components.Pages.DeploymentStrings>>();
        var agentEditor = context.Services.GetRequiredService<IStringLocalizer<Components.Pages.AgentEditorStrings>>();
        var agentRuns = context.Services.GetRequiredService<IStringLocalizer<Components.Pages.AgentRunsStrings>>();
        var agentRunnerInspector = context.Services.GetRequiredService<IStringLocalizer<Components.Pages.AgentRunnerInspectorStrings>>();
        var flows = context.Services.GetRequiredService<IStringLocalizer<Components.Pages.FlowsStrings>>();
        var flowRunDetails = context.Services.GetRequiredService<IStringLocalizer<Components.Pages.FlowRunDetailsStrings>>();
        var flowRuns = context.Services.GetRequiredService<IStringLocalizer<Components.Pages.FlowRunsStrings>>();
        var flowDetails = context.Services.GetRequiredService<IStringLocalizer<Components.Pages.FlowDetailsStrings>>();
        var flowOrchestrationEditor = context.Services.GetRequiredService<IStringLocalizer<Components.Pages.FlowOrchestrationEditorStrings>>();
        var newFlow = context.Services.GetRequiredService<IStringLocalizer<Components.Pages.NewFlowStrings>>();
        var modelProfileEditor = context.Services.GetRequiredService<IStringLocalizer<Components.Pages.ModelProfileEditorStrings>>();
        var modelProfiles = context.Services.GetRequiredService<IStringLocalizer<Components.Pages.ModelProfilesStrings>>();
        var modelProviderDetails = context.Services.GetRequiredService<IStringLocalizer<Components.Pages.ModelProviderDetailsStrings>>();
        var modelProviders = context.Services.GetRequiredService<IStringLocalizer<Components.Pages.ModelProvidersStrings>>();
        var runtimeProfileEditor = context.Services.GetRequiredService<IStringLocalizer<Components.Pages.RuntimeProfileEditorStrings>>();
        var runtimeProfiles = context.Services.GetRequiredService<IStringLocalizer<Components.Pages.RuntimeProfilesStrings>>();
        var toolDetails = context.Services.GetRequiredService<IStringLocalizer<Components.Pages.ToolDetailsStrings>>();
        var toolGovernanceAudit = context.Services.GetRequiredService<IStringLocalizer<Components.Pages.ToolGovernanceAuditStrings>>();
        var toolProviderEditor = context.Services.GetRequiredService<IStringLocalizer<Components.Pages.ToolProviderEditorStrings>>();
        var toolProviders = context.Services.GetRequiredService<IStringLocalizer<Components.Pages.ToolProvidersStrings>>();
        var tools = context.Services.GetRequiredService<IStringLocalizer<Components.Pages.ToolsStrings>>();
        var triggers = context.Services.GetRequiredService<IStringLocalizer<Components.Pages.TriggerStrings>>();

        Assert.AreEqual("Créer un agent", agents["CreateAgent"].Value);
        Assert.AreEqual("Définissez, publiez et exploitez des ressources d’agents spécialisés.", agents["Description"].Value);
        Assert.AreEqual("Gen", agents["GenerationHeader"].Value);
        Assert.AreEqual("1 déployé(s)", agents["DeployedCount", 1].Value);
        Assert.AreEqual("Déployé", agents["StatusDeployed"].Value);
        Assert.AreEqual("Prêt", deployments["Status.Ready"].Value);
        Assert.AreEqual("Non déployé", deployments["Status.NotDeployed"].Value);
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
        Assert.AreEqual("Exécutions de Flow", flowRuns["Title"].Value);
        Assert.AreEqual("En attente d’une saisie", flowRuns["Status.WaitingForInput"].Value);
        Assert.AreEqual("Déroulement", flowRunDetails["ExecutionTimeline"].Value);
        Assert.AreEqual("Synthèse", flowRunDetails["Summary"].Value);
        Assert.AreEqual("Entrée / sortie", flowRunDetails["InputOutputTab"].Value);
        Assert.AreEqual("3 événements", flowRunDetails["EventCount.Many", 3].Value);
        Assert.AreEqual("Réponse demandée", flowRunDetails["Event.InputRequested", "agent-1"].Value);
        Assert.AreEqual("Vue d’ensemble", flowDetails["Tab.Overview"].Value);
        Assert.AreEqual("2 réussies", flowDetails["SucceededRunCount.Many", 2].Value);
        Assert.AreEqual("Discussion de groupe", flowOrchestrationEditor["Strategy.GroupChat"].Value);
        Assert.AreEqual("2 sélectionnés", flowOrchestrationEditor["SelectedCount.Many", 2].Value);
        Assert.AreEqual("Nouveau Flow", newFlow["Title"].Value);
        Assert.AreEqual("Routage d’agents", newFlow["Template.AgentRouting"].Value);
        Assert.AreEqual("Créer un profil de modèle", modelProfileEditor["CreateModelProfile"].Value);
        Assert.AreEqual("Fournisseur et modèle", modelProfileEditor["ProviderAndModel"].Value);
        Assert.AreEqual("La modification de ce profil affectera 1 agent.", modelProfileEditor["ChangingProfileAffects", modelProfileEditor["AgentCount.One", 1].Value].Value);
        Assert.AreEqual("Résolution effective", modelProfileEditor["EffectiveResolution"].Value);
        Assert.AreEqual("Non pris en charge", modelProfileEditor["Support.Unsupported"].Value);
        Assert.AreEqual("Configuration non valide", modelProfileEditor["Status.InvalidConfiguration"].Value);
        Assert.AreEqual("Profils de modèles", modelProfiles["Title"].Value);
        Assert.AreEqual("Fournisseur indisponible", modelProfiles["Status.ProviderUnavailable"].Value);
        Assert.AreEqual("Ce profil de modèle est encore référencé par 2 agents.", modelProfiles["DeleteConflictMessage", modelProfiles["AgentCount.Many", 2].Value].Value);
        Assert.AreEqual("Fournisseurs de modèles", modelProviders["Title"].Value);
        Assert.AreEqual("2 modèles", modelProviders["ModelCount.Many", 2].Value);
        Assert.AreEqual("Point de terminaison détenu par l’extension", modelProviderDetails["ExtensionOwnedEndpoint"].Value);
        Assert.AreEqual("Ce fournisseur est référencé par 1 profil de modèle.", modelProviderDetails["ProviderReferencedBy", modelProviderDetails["ProfileCount.One", 1].Value].Value);
        Assert.AreEqual("Profils d’exécution", runtimeProfiles["Title"].Value);
        Assert.AreEqual("2 déploiements", runtimeProfiles["DeploymentCount.Many", 2].Value);
        Assert.AreEqual("Politique d’exécution", runtimeProfileEditor["ExecutionPolicy"].Value);
        Assert.AreEqual("Requise", runtimeProfileEditor["ToolInvocation.Required"].Value);
        Assert.AreEqual("Ce profil est utilisé par 1 déploiement. Les modifications prendront effet à la prochaine réconciliation.", runtimeProfileEditor["ProfileUsedWarning", runtimeProfileEditor["DeploymentCount.One", 1].Value].Value);
        Assert.AreEqual("Outils", tools["Title"].Value);
        Assert.AreEqual("Aucun outil découvert", tools["EmptyTitle"].Value);
        Assert.AreEqual("Fournisseurs d’outils", toolProviders["Title"].Value);
        Assert.AreEqual("Non découvert", toolProviders["Status.notDiscovered"].Value);
        Assert.AreEqual("Gouvernance et source", toolDetails["GovernanceAndSource"].Value);
        Assert.AreEqual("Approbation requise", toolDetails["RequiresApproval"].Value);
        Assert.AreEqual("Gouvernance des outils", toolGovernanceAudit["Title"].Value);
        Assert.AreEqual("Refusée", toolGovernanceAudit["Decision.Denied"].Value);
        Assert.AreEqual("Les arguments n’ont pas été conservés pour cette invocation.", toolGovernanceAudit["ArgumentsNotRetained"].Value);
        Assert.AreEqual("Séquence 4", toolGovernanceAudit["Sequence", 4].Value);
        Assert.AreEqual("Ajouter un fournisseur d’outils", toolProviderEditor["AddProvider"].Value);
        Assert.AreEqual("Connexion établie. La négociation du protocole a réussi. Outils découverts : 2 outils.", toolProviderEditor["ConnectionSucceeded", toolProviderEditor["ToolCount.Many", 2].Value].Value);
        Assert.AreEqual("Nouveau déclencheur", triggers["NewTrigger"].Value);
        Assert.AreEqual("2 déclencheurs", triggers["TriggerCount.Many", 2].Value);
        Assert.AreEqual("Ignorer pendant que le Work précédent est actif", triggers["Concurrency.SkipActive"].Value);
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
