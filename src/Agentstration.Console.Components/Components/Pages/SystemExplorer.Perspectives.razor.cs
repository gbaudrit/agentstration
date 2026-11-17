using Agentstration.Agents;
using Agentstration.Resources;
using Agentstration.SystemGraph.Contracts;
using Agentstration.SystemOperations.Contracts;
using Agentstration.Web.SystemExplorer;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace Agentstration.Web.Components.Pages;

public partial class SystemExplorer
{
    [SupplyParameterFromQuery(Name = "view")] public string? ViewQuery { get; set; }
    [SupplyParameterFromQuery(Name = "revision")] public string? VersionQuery { get; set; }
    [SupplyParameterFromQuery(Name = "compare")] public string? CompareQuery { get; set; }
    [SupplyParameterFromQuery(Name = "deployment")] public Guid? DeploymentQuery { get; set; }
    [SupplyParameterFromQuery(Name = "configuration")] public Guid? ConfigurationQuery { get; set; }
    private string Perspective => ViewQuery is "definition" or "deployment" ? ViewQuery : "resources";
    private AgentDefinitionCatalog? catalog;
    private IReadOnlyList<AgentDeploymentView> assignments = [];
    private AgentDeploymentView? assignment;
    private AgentDeploymentConfiguration? configuration;
    private SystemGraphDiagramOptions? diagramOptions;
    private SystemOperationalSnapshot? deploymentObservation;
    private SystemGraphPage? received;
    private string? graphRoot;
    private string[] definitionRoots = [];
    private string? perspectiveError;
    private bool observing;
    private string deploymentName = "", deploymentEnvironment = "production", deploymentTargetId = "", deploymentTargetName = "";
    private string deploymentTargetKind = "worker-pool", deploymentRuntime = "awp-worker", deploymentRevision = "";
    private string ActiveRoot => graphRoot ?? RootQuery ?? "";
    private bool HasPlacement => diagramOptions?.Placements?.Count > 0;
    private bool CanAssign => canWrite && contextScope.Kind == ResourceScopeKind.Workspace && catalog?.Revisions.Count > 0 && !loading && !busy;

    private void ResetPerspectiveState()
    {
        catalog = null; assignments = []; assignment = null; configuration = null; diagramOptions = null;
        deploymentObservation = null; received = null; graphRoot = null; definitionRoots = []; perspectiveError = null; observing = false;
        deploymentName = ""; deploymentEnvironment = "production"; deploymentTargetId = ""; deploymentTargetName = "";
        deploymentTargetKind = "worker-pool"; deploymentRuntime = "awp-worker"; deploymentRevision = "";
    }
    private async Task<IReadOnlyList<string?>> PreparePerspectiveAsync(ResourceScopeRef actor, ResourceNamespace ns, long current, CancellationToken ct)
    {
        catalog = null; assignments = []; assignment = null; configuration = null; definitionRoots = [];
        diagramOptions = null; deploymentObservation = null; perspectiveError = null; observing = false;
        graphRoot = RootQuery;
        if (Perspective == "resources") return [RootQuery];
        if (RootQuery?.StartsWith("resource:", StringComparison.Ordinal) == true && Guid.TryParse(RootQuery[9..], out var uid))
        {
            var definitions = await Client.DefinitionsAsync(actor, uid, ct);
            if (current != revision || ct.IsCancellationRequested) return [];
            catalog = definitions;
        }
        if (Perspective == "definition" && catalog is not null)
        {
            if (VersionQuery is not (null or "draft") && !catalog.Revisions.Any(node => node.Id == VersionQuery)
                || !string.IsNullOrEmpty(CompareQuery) && !catalog.Revisions.Any(node => node.Id == CompareQuery))
                throw new InvalidOperationException("The selected definition is unavailable.");
            var primary = VersionQuery == "draft" ? catalog.Agent : catalog.Revisions.FirstOrDefault(node => node.Id == VersionQuery)
                ?? catalog.Revisions.FirstOrDefault() ?? catalog.Agent;
            var other = catalog.Revisions.FirstOrDefault(node => node.Id == CompareQuery && node.Id != primary.Id);
            definitionRoots = other is null ? [primary.Id] : [primary.Id, other.Id]; graphRoot = primary.Id;
            diagramOptions = new("definition", graphRoot);
            return definitionRoots;
        }
        if (catalog is not null) deploymentRevision = catalog.Revisions.FirstOrDefault()?.Identity.ResourceUid.ToString("D") ?? "";
        if (Perspective == "deployment")
        {
            var available = await Client.DeploymentsAsync(actor, ns, catalog?.Agent.Identity.ResourceUid, ct);
            if (current != revision || ct.IsCancellationRequested) return [];
            assignments = available;
            assignment = DeploymentQuery is null ? assignments.FirstOrDefault() : assignments.FirstOrDefault(value => value.Uid == DeploymentQuery);
            configuration = ConfigurationQuery is null
                ? assignment?.Configurations.FirstOrDefault(value => value.Id == assignment.CurrentConfigurationId)
                : assignment?.Configurations.FirstOrDefault(value => value.Id == ConfigurationQuery);
            if (configuration is not null && assignment is not null)
            {
                graphRoot = $"deployment:{assignment.Uid:D}:{configuration.Id:D}";
                diagramOptions = new("deployment", graphRoot);
                deploymentEnvironment = configuration.Environment; deploymentTargetId = configuration.Target.Id;
                deploymentTargetName = configuration.Target.Name; deploymentTargetKind = configuration.Target.Kind;
                deploymentRuntime = configuration.Target.Runtime; deploymentRevision = configuration.RevisionUid.ToString("D");
                return [graphRoot];
            }
        }
        return [RootQuery];
    }
    private string PerspectiveUrl(string perspective, string? root = null, string? version = null, string? compare = null, Guid? deployment = null, Guid? config = null) =>
        QueryHelpers.AddQueryString("/system-explorer", new Dictionary<string, string?>
        {
            ["context"] = ContextQuery ?? NavigationContext.ScopeRef ?? contextScope.Value,
            ["namespace"] = NamespaceQuery ?? resourceNamespace.Value, ["scopeRef"] = OwnerQuery,
            ["root"] = root ?? RootQuery, ["view"] = perspective == "resources" ? null : perspective,
            ["revision"] = version, ["compare"] = compare, ["deployment"] = deployment?.ToString("D"),
            ["configuration"] = config?.ToString("D"), ["renderer"] = showDiagram ? null : "list"
        });
    private void ChangeDefinition(ChangeEventArgs args) => Navigation.NavigateTo(PerspectiveUrl("definition", version: args.Value?.ToString(), compare: CompareQuery));
    private void ChangeComparison(ChangeEventArgs args) => Navigation.NavigateTo(PerspectiveUrl("definition", version: VersionQuery, compare: args.Value?.ToString()));
    private void ChangeDeployment(ChangeEventArgs args) => Navigation.NavigateTo(PerspectiveUrl("deployment", deployment: Guid.TryParse(args.Value?.ToString(), out var uid) ? uid : null));
    private void ChangeConfiguration(ChangeEventArgs args) => Navigation.NavigateTo(PerspectiveUrl("deployment", deployment: assignment?.Uid, config: Guid.TryParse(args.Value?.ToString(), out var uid) ? uid : null));
    private void ChooseAgent(ChangeEventArgs args) => Navigation.NavigateTo(PerspectiveUrl(Perspective, root: args.Value?.ToString()));
    private async Task SaveAssignmentAsync(bool create)
    {
        if (!CanAssign || !Guid.TryParse(deploymentRevision, out var revisionUid)) return;
        var current = revision; busy = true; perspectiveError = null;
        try
        {
            var value = await Client.AssignAsync(contextScope, new(create ? deploymentName : assignment!.Name, revisionUid, deploymentEnvironment,
                new(deploymentTargetId, deploymentTargetName, deploymentTargetKind, deploymentRuntime), contextScope, resourceNamespace), create ? null : assignment!.ETag, create, cancellation!.Token);
            if (current == revision) Navigation.NavigateTo(PerspectiveUrl("deployment", deployment: value.Uid, config: value.CurrentConfigurationId));
        }
        catch (OperationCanceledException) when (current != revision || cancellation!.IsCancellationRequested) { }
        catch (Exception) { if (current == revision) perspectiveError = T("DeploymentSaveFailed"); }
        finally { if (current == revision) busy = false; }
    }
    private async Task ObserveDeploymentAsync()
    {
        if (configuration is null || assignment is null || cancellation is null || observing) return;
        observing = true; perspectiveError = null; var current = revision;
        try
        {
            var value = await Client.ObservedDeploymentAsync(contextScope, assignment.Namespace, ActiveRoot, cancellation.Token);
            if (current != revision) return;
            deploymentObservation = value;
            var placements = value.Facts is { CanInspectWorkerDetails: true } facts ? facts.Runs.Where(run => run.DeploymentUid == assignment.Uid && run.DeploymentConfigurationId == configuration.Id
                && run.RevisionUid == configuration.RevisionUid && run.DefinitionHash == configuration.DefinitionHash)
                .SelectMany(run => run.Placements.SelectMany(placement => placement.Attempts.Select(attempt => new SystemGraphObservedPlacement(
                    attempt.WorkerId, attempt.SessionId, run.RunId, placement.AssignmentId, attempt.AttemptId,
                    attempt.LastHeartbeatAt, attempt.LeaseExpiresAt, attempt.Presence))))
                .DistinctBy(value => (value.WorkerId, value.SessionId)).Take(20).ToArray() : [];
            diagramOptions = new("observed", ActiveRoot, placements);
            diagramFailed = false;
        }
        catch (OperationCanceledException) when (current != revision || cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (current == revision) { diagramOptions = new("observed", ActiveRoot, []); deploymentObservation = null; perspectiveError = T("DeploymentObservedFailed"); }
        }
        finally { if (current == revision) observing = false; }
    }
    private void ShowDesiredDeployment() { diagramOptions = new("deployment", ActiveRoot); deploymentObservation = null; diagramFailed = false; }
}
