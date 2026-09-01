using Agentstration.Flow;
using Agentstration.Flow.Contracts;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Management.Core;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Security.AspNetCoreIdentity;
using Agentstration.Work;
using Agentstration.Work.Contracts;

namespace Agentstration.Web.Configuration;

internal sealed record OpenApiSuccessResponse(
    int StatusCode,
    Type? BodyType,
    string Summary,
    string ResponseDescription,
    string MediaType = "application/json",
    string? Description = null);

internal static class OpenApiSuccessResponseCatalog
{
    public static OpenApiSuccessResponse? Resolve(string method, string path)
    {
        var verb = method.ToUpperInvariant();
        return Flow(verb, path)
            ?? Runtime(verb, path)
            ?? ToolGovernance(verb, path)
            ?? Work(verb, path)
            ?? WorkOperations(verb, path)
            ?? Workplace(verb, path)
            ?? Management(verb, path)
            ?? ModelManagement(verb, path)
            ?? Bootstrap(verb, path)
            ?? Identity(verb, path);
    }

    private static OpenApiSuccessResponse? Flow(string method, string path)
    {
        if (!path.StartsWith("/api/flows", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/api/namespaces/{namespace}/flows", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/api/flowRuns", StringComparison.OrdinalIgnoreCase)) return null;

        if (method == "DELETE") return NoContent("Delete the Flow", "The Flow was deleted.");
        if (path.StartsWith("/api/flowRuns", StringComparison.OrdinalIgnoreCase))
        {
            if (path == "/api/flowRuns" && method == "GET") return Json<FlowRunPageResponse>(200, "List Flow Runs");
            if (path.EndsWith("/eventHistory", StringComparison.OrdinalIgnoreCase)) return Json<IReadOnlyList<FlowRunEvent>>(200, "List Flow Run events");
            if (path.EndsWith("/inputs/{inputId}/response", StringComparison.OrdinalIgnoreCase)) return Json<FlowRun>(202, "Respond to a Flow Run input");
            if (path.EndsWith("/inputs/{inputId}", StringComparison.OrdinalIgnoreCase)) return Json<InputRequest>(200, "Get a Flow Run input");
            if (path.EndsWith("/inputs", StringComparison.OrdinalIgnoreCase)) return Json<IReadOnlyList<InputRequest>>(200, "List Flow Run inputs");
            if (path.EndsWith("/cancel", StringComparison.OrdinalIgnoreCase)) return Json<FlowRun>(200, "Cancel a Flow Run");
            if (!path.EndsWith("/events", StringComparison.OrdinalIgnoreCase)) return Json<FlowRun>(200, "Get a Flow Run");
            return new(200, typeof(string), "Stream Flow Run events", "Server-sent event stream.", "text/event-stream");
        }

        if (path.EndsWith("/draft/runs", StringComparison.OrdinalIgnoreCase)) return Json<FlowRun>(202, "Run a Flow draft");
        if (path.EndsWith("/runs/{runId}/cancel", StringComparison.OrdinalIgnoreCase)) return Json<FlowRun>(200, "Cancel a Flow Run");
        if (path.EndsWith("/runs/{runId}", StringComparison.OrdinalIgnoreCase)) return Json<FlowRun>(200, "Get a Flow Run");
        if (path.EndsWith("/runs", StringComparison.OrdinalIgnoreCase))
            return method == "POST" ? Json<FlowRun>(202, "Start a Flow Run") : Json<FlowRunPageResponse>(200, "List Flow Runs");
        if (path.EndsWith("/versions/{version}/draft", StringComparison.OrdinalIgnoreCase)) return Json<FlowDraftResponse>(200, "Create a draft from a published Flow version");
        if (path.EndsWith("/versions/{version}", StringComparison.OrdinalIgnoreCase)) return Json<FlowVersionResponse>(200, "Get a published Flow version");
        if (path.EndsWith("/versions", StringComparison.OrdinalIgnoreCase))
            return method == "POST" ? Json<FlowVersionResponse>(201, "Publish a Flow version") : Json<IReadOnlyList<FlowVersionResponse>>(200, "List published Flow versions");
        if (path.EndsWith("/draft/source", StringComparison.OrdinalIgnoreCase))
            return method == "GET" ? Json<FlowSourceResponse>(200, "Get Flow draft source") : Json<FlowDraftResponse>(200, "Replace Flow draft source");
        if (path.EndsWith("/draft", StringComparison.OrdinalIgnoreCase))
            return method == "GET" ? Json<FlowDraftResponse>(200, "Get a Flow draft") : Json<FlowDraftResponse>(200, "Update a Flow draft");
        if (path.EndsWith("/validate", StringComparison.OrdinalIgnoreCase)) return Json<FlowValidationResponse>(200, "Validate a Flow draft");
        if (path.EndsWith("/publish", StringComparison.OrdinalIgnoreCase)) return Json<FlowVersionResponse>(201, "Publish a Flow draft");
        if (path.EndsWith("/drafts", StringComparison.OrdinalIgnoreCase)) return Json<FlowDraftResponse>(201, "Create a Flow draft");

        var collection = path == "/api/flows" || path == "/api/namespaces/{namespace}/flows";
        if (collection) return method == "POST" ? Json<FlowResponse>(201, "Create a Flow") : Json<FlowPageResponse>(200, "List Flows");
        return method switch
        {
            "GET" => Json<FlowResponse>(200, "Get a Flow"),
            "PUT" => Json<FlowResponse>(200, "Update a Flow"),
            _ => null
        };
    }

    private static OpenApiSuccessResponse? Runtime(string method, string path)
    {
        if (!path.StartsWith("/api/runtime/", StringComparison.OrdinalIgnoreCase)) return null;
        if (path.EndsWith("/events", StringComparison.OrdinalIgnoreCase))
            return new(200, typeof(string), "Stream Runtime Run events", "Server-sent event stream.", "text/event-stream");
        if (path.EndsWith("/eventHistory", StringComparison.OrdinalIgnoreCase)) return Json<IReadOnlyList<RuntimeRunEvent>>(200, "List Runtime Run events");
        if (path.EndsWith("/readiness", StringComparison.OrdinalIgnoreCase)) return Json<AgentRuntimeReadinessResponse>(200, "Get agent runtime readiness");
        if (path.EndsWith("/prepare", StringComparison.OrdinalIgnoreCase)) return Json<PrepareAgentRuntimeResponse>(200, "Prepare an agent runtime");
        if (path.EndsWith("/cancel", StringComparison.OrdinalIgnoreCase)) return Json<RuntimeRun>(200, "Cancel a Runtime Run");
        if (path.EndsWith("/retry", StringComparison.OrdinalIgnoreCase)) return Json<RuntimeRun>(202, "Retry a Runtime Run");
        if (path == "/api/runtime/runs")
            return method == "POST" ? Json<RuntimeRun>(202, "Create a Runtime Run") : Json<RuntimeRunPageResponse>(200, "List Runtime Runs");
        if (path.StartsWith("/api/runtime/runs/", StringComparison.OrdinalIgnoreCase)) return Json<RuntimeRun>(200, "Get a Runtime Run");
        return null;
    }

    private static OpenApiSuccessResponse? ToolGovernance(string method, string path)
    {
        if (method != "GET" || !path.StartsWith("/api/tool-governance/", StringComparison.OrdinalIgnoreCase)) return null;
        return Json<ToolGovernanceAuditPage>(200, "List tool governance audit records");
    }

    private static OpenApiSuccessResponse? Work(string method, string path)
    {
        if (!path.StartsWith("/api/work/workitems", StringComparison.OrdinalIgnoreCase)) return null;
        if (path == "/api/work/workitems")
            return method == "POST" ? Json<WorkItemResponse>(201, "Create a Work Item") : Json<WorkItemPageResponse>(200, "List Work Items");
        if (path.EndsWith("/events", StringComparison.OrdinalIgnoreCase)) return Json<IReadOnlyList<WorkEventResponse>>(200, "List Work Item events");
        if (path.EndsWith("/result", StringComparison.OrdinalIgnoreCase)) return Json<WorkResultResponse>(200, "Get the Work Item result");
        if (path.EndsWith("/cancel", StringComparison.OrdinalIgnoreCase)) return Json<WorkItemResponse>(200, "Cancel a Work Item");
        if (path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase)) return Json<WorkItemResponse>(200, "Add a Work Item message");
        if (path.EndsWith("/input", StringComparison.OrdinalIgnoreCase)) return Json<WorkItemResponse>(200, "Provide Work Item input");
        if (path.EndsWith("/approval", StringComparison.OrdinalIgnoreCase)) return Json<WorkItemResponse>(200, "Submit a Work Item approval");
        return Json<WorkItemResponse>(200, "Get a Work Item");
    }

    private static OpenApiSuccessResponse? WorkOperations(string method, string path)
    {
        if (!path.StartsWith("/api/tasks", StringComparison.OrdinalIgnoreCase)) return null;
        if (path == "/api/tasks") return Json<WorkTaskOperationsPageResponse>(200, "List operational Tasks");
        if (path == "/api/tasks/summary") return Json<WorkTaskOperationsCountersResponse>(200, "Get operational Task counters");
        if (path.EndsWith("/activities", StringComparison.OrdinalIgnoreCase)) return Json<IReadOnlyList<WorkTaskActivity>>(200, "List Task activities");
        if (path.EndsWith("/flow-runs/{runId}", StringComparison.OrdinalIgnoreCase)) return Json<FlowRun>(200, "Get a Task Flow Run");
        if (path.EndsWith("/flow-runs", StringComparison.OrdinalIgnoreCase)) return Json<IReadOnlyList<WorkTaskFlowRunResponse>>(200, "List Task Flow Runs");
        if (path.EndsWith("/pending-actions/{actionId}/respond", StringComparison.OrdinalIgnoreCase)) return Json<PendingActionContract>(200, "Respond to a Task pending action");
        if (path.EndsWith("/pending-actions", StringComparison.OrdinalIgnoreCase)) return Json<IReadOnlyList<PendingActionContract>>(200, "List Task pending actions");
        if (path.EndsWith("/results", StringComparison.OrdinalIgnoreCase)) return Json<IReadOnlyList<WorkTaskResultResponse>>(200, "List Task results");
        if (path.EndsWith("/artifacts", StringComparison.OrdinalIgnoreCase)) return Json<IReadOnlyList<WorkTaskArtifactResponse>>(200, "List Task artifacts");
        if (path.EndsWith("/pause", StringComparison.OrdinalIgnoreCase)) return Json<WorkTask>(200, "Pause a Task");
        if (path.EndsWith("/resume", StringComparison.OrdinalIgnoreCase)) return Json<WorkTask>(200, "Resume a Task");
        if (path.EndsWith("/cancel", StringComparison.OrdinalIgnoreCase)) return Json<WorkTask>(200, "Cancel a Task");
        return Json<WorkTaskOperationsDetailResponse>(200, "Get operational Task details");
    }

    private static OpenApiSuccessResponse? Workplace(string method, string path)
    {
        if (path == "/api/workplace/workspaces") return Json<IReadOnlyList<WorkplaceWorkspaceResponse>>(200, "List Workplace workspaces");
        if (path.StartsWith("/api/entries", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/entries", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/resources", StringComparison.OrdinalIgnoreCase))
            return Entry(method, path);
        if (path.StartsWith("/api/management/workspaces/", StringComparison.OrdinalIgnoreCase)) return DashboardAdministration(method, path);
        if (!path.StartsWith("/api/workspaces/{workspaceName}", StringComparison.OrdinalIgnoreCase)) return null;

        if (path == "/api/workspaces/{workspaceName}") return Json<WorkplaceWorkspaceResponse>(200, "Get a Workplace workspace");
        if (path.EndsWith("/dashboards", StringComparison.OrdinalIgnoreCase)) return Json<IReadOnlyList<WorkplaceDashboardResponse>>(200, "List Workplace dashboards");
        if (path.EndsWith("/dashboard", StringComparison.OrdinalIgnoreCase)
            || path.Contains("/dashboards/", StringComparison.OrdinalIgnoreCase)) return Json<WorkplaceDashboardResponse>(200, "Get a Workplace dashboard");
        if (path.EndsWith("/interactions", StringComparison.OrdinalIgnoreCase)) return Json<InteractionPageResponse>(200, "List Workplace interactions");
        if (path.EndsWith("/interactions/{interactionId}", StringComparison.OrdinalIgnoreCase)) return Json<InteractionResponse>(200, "Get a Workplace interaction");
        if (path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase))
            return method == "POST" ? Json<AddConversationMessageResponse>(202, "Add a conversation message") : Json<IReadOnlyList<ConversationMessage>>(200, "List conversation messages");
        if (path.EndsWith("/pending-actions/{pendingActionId}/responses", StringComparison.OrdinalIgnoreCase)) return Json<PendingActionResolutionResponse>(200, "Respond to a pending action");
        if (path.EndsWith("/pending-actions", StringComparison.OrdinalIgnoreCase)) return Json<IReadOnlyList<PendingActionContract>>(200, "List pending actions");
        if (path.EndsWith("/tasks", StringComparison.OrdinalIgnoreCase)) return Json<WorkTaskPageResponse>(200, "List Workplace Tasks");
        if (path.EndsWith("/tasks/{taskId}", StringComparison.OrdinalIgnoreCase)) return Json<WorkTaskResponse>(200, "Get a Workplace Task");
        if (path.EndsWith("/tasks/{taskId}/pause", StringComparison.OrdinalIgnoreCase)) return Json<WorkTaskResponse>(200, "Pause a Workplace Task");
        if (path.EndsWith("/tasks/{taskId}/resume", StringComparison.OrdinalIgnoreCase)) return Json<WorkTaskResponse>(200, "Resume a Workplace Task");
        if (path.EndsWith("/tasks/{taskId}/cancel", StringComparison.OrdinalIgnoreCase)) return Json<WorkTaskResponse>(200, "Cancel a Workplace Task");
        if (path.EndsWith("/activities", StringComparison.OrdinalIgnoreCase)) return Json<IReadOnlyList<WorkTaskActivity>>(200, "List Workplace Task activities");
        if (path.EndsWith("/results", StringComparison.OrdinalIgnoreCase)) return Json<IReadOnlyList<WorkTaskResult>>(200, "List Workplace Task results");
        if (path.EndsWith("/artifacts", StringComparison.OrdinalIgnoreCase)) return Json<IReadOnlyList<WorkTaskArtifact>>(200, "List Workplace Task artifacts");
        if (path.EndsWith("/content", StringComparison.OrdinalIgnoreCase)) return Binary(200, "Download a Workplace Task artifact");
        if (path.EndsWith("/notifications/unread-count", StringComparison.OrdinalIgnoreCase)) return Json<UnreadNotificationCountResponse>(200, "Get unread notification count");
        if (path.EndsWith("/notifications/read-all", StringComparison.OrdinalIgnoreCase)) return NoContent("Mark all notifications as read");
        if (path.EndsWith("/read", StringComparison.OrdinalIgnoreCase)) return Json<WorkNotification>(200, "Mark a notification as read");
        if (path.EndsWith("/notifications", StringComparison.OrdinalIgnoreCase)) return Json<WorkNotificationPageResponse>(200, "List Workplace notifications");
        return null;
    }

    private static OpenApiSuccessResponse? Entry(string method, string path)
    {
        if (path.EndsWith("/interactions", StringComparison.OrdinalIgnoreCase)) return Json<EntrySubmissionResponse>(201, "Submit an Entry interaction");
        if (path == "/api/entries") return Json<IReadOnlyList<EntryResponse>>(200, "List published Entries");
        if (path == "/api/resources") return Json<IReadOnlyList<ResourcePickerItem>>(200, "List resources available to Entries");
        if (path.EndsWith("/dependencies", StringComparison.OrdinalIgnoreCase)) return Json<IReadOnlyList<EntryDependencyResponse>>(200, "List Entry dependencies");
        if (path.EndsWith("/validate", StringComparison.OrdinalIgnoreCase)) return Json<EntryValidationResponse>(200, "Validate an Entry draft");
        if (path.EndsWith("/publish", StringComparison.OrdinalIgnoreCase)) return Json<EntryResponse>(200, "Publish an Entry draft");
        if (path.Contains("/management/entries", StringComparison.OrdinalIgnoreCase))
        {
            if (path.EndsWith("/entries", StringComparison.OrdinalIgnoreCase)) return Json<IReadOnlyList<EntryDraftResponse>>(200, "List Entry drafts");
            return Json<EntryDraftResponse>(200, method == "PUT" ? "Update an Entry draft" : "Get an Entry draft");
        }
        return Json<EntryResponse>(200, "Get a published Entry");
    }

    private static OpenApiSuccessResponse? DashboardAdministration(string method, string path)
    {
        if (method == "DELETE") return NoContent("Delete a dashboard draft");
        if (path.EndsWith("/dashboards", StringComparison.OrdinalIgnoreCase)) return Json<IReadOnlyList<WorkplaceDashboardDraftResponse>>(200, "List dashboard drafts");
        if (path.EndsWith("/publish", StringComparison.OrdinalIgnoreCase)) return Json<WorkplaceDashboardResponse>(200, "Publish a dashboard draft");
        return Json<WorkplaceDashboardDraftResponse>(200, method == "PUT" ? "Update a dashboard draft" : "Get a dashboard draft");
    }

    private static OpenApiSuccessResponse? Management(string method, string path)
    {
        if (path.Contains("/agents", StringComparison.OrdinalIgnoreCase))
        {
            if (method == "DELETE") return NoContent("Delete the agent resource");
            if (path.EndsWith("/model", StringComparison.OrdinalIgnoreCase)) return Json<AgentModelResponse>(200, "Get the agent model resolution");
            if (path.EndsWith("/purge-impact", StringComparison.OrdinalIgnoreCase)) return Json<AgentRevisionPurgeImpactResponse>(200, "Get agent revision purge impact");
            if (path.EndsWith("/revisions", StringComparison.OrdinalIgnoreCase)) return Json<AgentRevision>(201, "Create an agent revision");
            var collection = path.EndsWith("/agents", StringComparison.OrdinalIgnoreCase);
            if (collection) return Json<PagedResponse<AgentResource>>(200, "List agents");
            return Json<AgentResource>(200, method == "PUT" ? "Create or update an agent" : "Get an agent");
        }
        if (path.Contains("/deployments", StringComparison.OrdinalIgnoreCase))
        {
            if (path.EndsWith("/deployments", StringComparison.OrdinalIgnoreCase)) return Json<PagedResponse<AgentDeployment>>(200, "List deployments");
            if (path.EndsWith("/start", StringComparison.OrdinalIgnoreCase)) return Json<AgentDeployment>(202, "Start a deployment");
            if (path.EndsWith("/stop", StringComparison.OrdinalIgnoreCase)) return Json<AgentDeployment>(202, "Stop a deployment");
            if (path.EndsWith("/reconcile", StringComparison.OrdinalIgnoreCase)) return Json<AgentDeployment>(202, "Reconcile a deployment");
            return Json<AgentDeployment>(method == "POST" ? 202 : 200, method == "POST" ? "Create a deployment" : "Get a deployment");
        }
        if (path == "/api/routing/invoke") return Json<RouteAndExecuteResponse>(200, "Route and execute an input");
        if (path.Contains("/triggers", StringComparison.OrdinalIgnoreCase))
        {
            if (method == "DELETE") return NoContent("Delete a Trigger");
            if (path.EndsWith("/run", StringComparison.OrdinalIgnoreCase)) return Json<TriggerOccurrence>(202, "Run a Trigger now");
            if (path.EndsWith("/occurrences", StringComparison.OrdinalIgnoreCase)) return Json<IReadOnlyList<TriggerOccurrence>>(200, "List Trigger occurrences");
            if (path.EndsWith("/triggers", StringComparison.OrdinalIgnoreCase)) return Json<IReadOnlyList<TriggerResource>>(200, "List Triggers");
            return Json<TriggerResource>(200, method == "PUT" ? "Create or update a Trigger" : "Get a Trigger");
        }
        return Pack(method, path);
    }

    private static OpenApiSuccessResponse? Pack(string method, string path)
    {
        if (!path.StartsWith("/api/packs", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith("/api/pack-projects", StringComparison.OrdinalIgnoreCase)) return null;
        if (path.EndsWith("/download", StringComparison.OrdinalIgnoreCase))
            return new(200, typeof(string), "Download a Pack build", "Pack build ZIP archive.", "application/zip");
        if (method == "DELETE") return NoContent("Uninstall a Pack");
        if (path == "/api/packs/preview") return Json<PackInstallationPreview>(200, "Preview a Pack installation");
        if (path == "/api/packs") return method == "POST" ? Json<PackConfigurationResource>(201, "Install a Pack") : Json<IReadOnlyList<PackConfigurationResource>>(200, "List installed Packs");
        if (path.EndsWith("/source", StringComparison.OrdinalIgnoreCase)) return Json<PackConfigurationResource>(200, "Attach Pack source");
        if (path.EndsWith("/fork", StringComparison.OrdinalIgnoreCase)) return Json<PackProjectResource>(201, "Fork an installed Pack");
        if (path.StartsWith("/api/packs/", StringComparison.OrdinalIgnoreCase)) return Json<PackConfigurationResource>(200, "Get an installed Pack");
        if (path.EndsWith("/composer/resources", StringComparison.OrdinalIgnoreCase)) return Json<IReadOnlyList<PackCompositionCatalogItem>>(200, "List Pack composition resources");
        if (path.EndsWith("/composer/preview", StringComparison.OrdinalIgnoreCase)) return Json<PackCompositionPreview>(200, "Preview Pack composition");
        if (path.EndsWith("/builds/{buildId}/preview", StringComparison.OrdinalIgnoreCase)) return Json<PackInstallationPreview>(200, "Preview a Pack build installation");
        if (path.EndsWith("/builds/{buildId}/install", StringComparison.OrdinalIgnoreCase)) return Json<PackConfigurationResource>(201, "Install a Pack build");
        if (path.EndsWith("/builds", StringComparison.OrdinalIgnoreCase))
            return method == "POST" ? Json<PackProjectBuildResource>(201, "Build a Pack Project") : Json<IReadOnlyList<PackProjectBuildResource>>(200, "List Pack Project builds");
        if (path == "/api/pack-projects") return method == "POST" ? Json<PackProjectResource>(201, "Create a Pack Project") : Json<IReadOnlyList<PackProjectResource>>(200, "List Pack Projects");
        return Json<PackProjectResource>(200, method == "PUT" ? "Update a Pack Project" : "Get a Pack Project");
    }

    private static OpenApiSuccessResponse? ModelManagement(string method, string path)
    {
        if (method == "DELETE" && IsModelManagementPath(path)) return NoContent("Delete the resource");
        if (path == "/api/extensions") return Json<ValueResponse<ExtensionResponse>>(200, "List extensions");
        if (path == "/api/extensions/discover") return Json<ExtensionDiscoveryResponse>(200, "Discover extensions");
        if (path.StartsWith("/api/extensionregistrations", StringComparison.OrdinalIgnoreCase))
            return path == "/api/extensionregistrations"
                ? method == "POST" ? Json<ExtensionRegistrationResource>(201, "Create an extension registration") : Json<ValueResponse<ExtensionRegistrationResource>>(200, "List extension registrations")
                : Json<ExtensionRegistrationResource>(200, method == "PUT" ? "Update an extension registration" : "Get an extension registration");
        if (path.StartsWith("/api/modelproviders", StringComparison.OrdinalIgnoreCase))
        {
            if (path == "/api/modelproviders") return method == "POST" ? Json<ModelProviderResource>(201, "Create a model provider") : Json<ValueResponse<ModelProviderResponse>>(200, "List model providers");
            if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase)) return Json<ValueResponse<AvailableModelResponse>>(200, "List provider models");
            if (path.EndsWith("/status", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/test", StringComparison.OrdinalIgnoreCase)) return Json<ModelProviderStatusResponse>(200, "Get model provider status");
            if (path.EndsWith("/usages", StringComparison.OrdinalIgnoreCase)) return Json<ModelProviderUsagesResponse>(200, "List model provider usages");
            return Json<ModelProviderResource>(200, method == "PUT" ? "Update a model provider" : "Get a model provider");
        }
        if (path.StartsWith("/api/modelprofiles", StringComparison.OrdinalIgnoreCase))
        {
            if (path == "/api/modelprofiles") return method == "POST" ? Json<ModelProfileResource>(201, "Create a model profile") : Json<ValueResponse<ModelProfileSummaryResponse>>(200, "List model profiles");
            if (path.EndsWith("/usages", StringComparison.OrdinalIgnoreCase)) return Json<ModelProfileUsagesResponse>(200, "List model profile usages");
            if (path.EndsWith("/resolution", StringComparison.OrdinalIgnoreCase)) return Json<ModelProfileResolutionResponse>(200, "Resolve a model profile");
            if (path.EndsWith("/preview", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/apply", StringComparison.OrdinalIgnoreCase)) return Json<ModelProfileOptionMigrationPreviewResponse>(200, "Migrate model profile options");
            return Json<ModelProfileResource>(200, method == "PUT" ? "Update a model profile" : "Get a model profile");
        }
        if (path.StartsWith("/api/runtimeprofiles", StringComparison.OrdinalIgnoreCase))
        {
            if (path == "/api/runtimeprofiles") return method == "POST" ? Json<RuntimeProfileResource>(201, "Create a runtime profile") : Json<ValueResponse<RuntimeProfileSummaryResponse>>(200, "List runtime profiles");
            if (path.EndsWith("/usages", StringComparison.OrdinalIgnoreCase)) return Json<RuntimeProfileUsagesResponse>(200, "List runtime profile usages");
            return Json<RuntimeProfileResource>(200, method == "PUT" ? "Update a runtime profile" : "Get a runtime profile");
        }
        if (path.StartsWith("/api/toolproviders", StringComparison.OrdinalIgnoreCase))
        {
            if (path == "/api/toolproviders") return method == "POST" ? Json<ToolProviderResource>(201, "Create a Tool Provider") : Json<ValueResponse<ToolProviderResource>>(200, "List Tool Providers");
            if (path.EndsWith("/test", StringComparison.OrdinalIgnoreCase)) return Json<ToolConnectionTestResponse>(200, "Test a Tool Provider connection");
            if (path.EndsWith("/refresh", StringComparison.OrdinalIgnoreCase)) return Json<ToolDiscoveryDiffResponse>(200, "Refresh a Tool Provider catalog");
            if (path.EndsWith("/tools", StringComparison.OrdinalIgnoreCase)) return Json<ValueResponse<ToolResource>>(200, "List Tool Provider tools");
            return Json<ToolProviderResource>(200, method == "PUT" ? "Update a Tool Provider" : "Get a Tool Provider");
        }
        if (path.StartsWith("/api/tools", StringComparison.OrdinalIgnoreCase))
            return path == "/api/tools" ? Json<ValueResponse<ToolResource>>(200, "List Tools") : Json<ToolResource>(200, "Get or update a Tool");
        if (path.StartsWith("/api/toolexecutionhooks", StringComparison.OrdinalIgnoreCase))
            return path == "/api/toolexecutionhooks" ? method == "POST" ? Json<ToolExecutionHookResource>(201, "Create a Tool execution hook") : Json<ValueResponse<ToolExecutionHookResource>>(200, "List Tool execution hooks") : Json<ToolExecutionHookResource>(200, "Get or update a Tool execution hook");
        if (path.StartsWith("/api/vaults", StringComparison.OrdinalIgnoreCase))
        {
            if (path == "/api/vaults") return method == "POST" ? Json<VaultResource>(201, "Create a Vault") : Json<IReadOnlyList<VaultResponse>>(200, "List Vaults");
            if (path.EndsWith("/initialize", StringComparison.OrdinalIgnoreCase)) return Json<VaultInitializationResponse>(200, "Initialize a Vault");
            return Json<VaultResponse>(200, "Get or update a Vault");
        }
        if (path.StartsWith("/api/secrets", StringComparison.OrdinalIgnoreCase))
        {
            if (path.EndsWith("/value", StringComparison.OrdinalIgnoreCase)) return NoContent(method == "PUT" ? "Set a Secret value" : "Delete a Secret value");
            if (path == "/api/secrets") return method == "POST" ? Json<SecretResource>(201, "Create a Secret") : Json<IReadOnlyList<SecretResponse>>(200, "List Secrets");
            if (path.EndsWith("/usages", StringComparison.OrdinalIgnoreCase)) return Json<SecretUsagesResponse>(200, "List Secret usages");
            return Json<SecretResponse>(200, "Get or update a Secret");
        }
        return null;
    }

    private static OpenApiSuccessResponse? Identity(string method, string path)
    {
        if (path.StartsWith("/api/auth", StringComparison.OrdinalIgnoreCase))
        {
            if (path.EndsWith("/bootstrap", StringComparison.OrdinalIgnoreCase)) return method == "GET" ? Json<BootstrapStatusResponse>(200, "Get bootstrap status") : Json<PrincipalIdentifierResponse>(201, "Bootstrap the first administrator");
            if (path.EndsWith("/local/login", StringComparison.OrdinalIgnoreCase)) return NoContent("Sign in with a local account");
            if (path.EndsWith("/logout", StringComparison.OrdinalIgnoreCase)) return NoContent("Sign out");
            return new(302, null, "Start OIDC sign-in", "The browser is redirected to the configured identity provider.");
        }
        if (!path.StartsWith("/api/identity", StringComparison.OrdinalIgnoreCase)) return null;
        if (path.EndsWith("/pat/{tokenId}", StringComparison.OrdinalIgnoreCase))
            return NoContent(path.Contains("/principals/", StringComparison.OrdinalIgnoreCase)
                ? "Revoke a principal personal access token"
                : "Revoke a personal access token");
        if (path.EndsWith("/pat", StringComparison.OrdinalIgnoreCase))
        {
            var principalAdministration = path.Contains("/principals/", StringComparison.OrdinalIgnoreCase);
            return method switch
            {
                "GET" => Json<IReadOnlyList<PersonalAccessTokenResponse>>(200,
                    principalAdministration ? "List a principal's personal access tokens" : "List personal access tokens"),
                "POST" => Json<CreatedPersonalAccessTokenResponse>(201, "Create a personal access token"),
                "DELETE" => Json<RevokedPersonalAccessTokensResponse>(200,
                    principalAdministration ? "Revoke all personal access tokens for a principal" : "Revoke all personal access tokens"),
                _ => null
            };
        }
        if (path.StartsWith("/api/identity/accounts", StringComparison.OrdinalIgnoreCase))
            return path == "/api/identity/accounts" && method == "GET" ? Json<IReadOnlyList<LocalAccountView>>(200, "List local accounts") : Json<LocalAccountView>(method == "POST" ? 201 : 200, method == "POST" ? "Create a local account" : "Update local account status");
        if (path == "/api/identity/context") return Json<ConsoleContextView>(200, "Get the current identity context");
        if (path == "/api/identity/context/workspace") return Json<ConsoleContextView>(200, "Select the current workspace");
        if (path == "/api/identity/preferences") return Json<PrincipalPreferencesResponse>(200, method == "PUT" ? "Update principal preferences" : "Get principal preferences");
        if (path == "/api/identity/organization") return Json<TenantAdministrationView>(200, "Get organization administration details");
        if (path == "/api/identity/workspaces") return method == "POST" ? Json<Workspace>(201, "Create a workspace") : Json<IReadOnlyList<ConsoleWorkspaceView>>(200, "List identity workspaces");
        if (path.EndsWith("/memberships/{principalId}", StringComparison.OrdinalIgnoreCase)) return method == "DELETE" ? NoContent("Remove a workspace membership") : Json<WorkspaceMemberView>(200, "Set a workspace membership");
        if (path.EndsWith("/memberships", StringComparison.OrdinalIgnoreCase)) return Json<IReadOnlyList<WorkspaceMemberView>>(200, "List workspace memberships");
        if (path.StartsWith("/api/identity/workspaces/", StringComparison.OrdinalIgnoreCase)) return Json<Workspace>(200, "Get a workspace");
        if (path == "/api/identity/members") return Json<IReadOnlyList<MemberAdministrationView>>(200, "List organization members");
        if (path == "/api/identity/platform") return Json<PlatformRoleResponse>(200, "Get platform role");
        if (path == "/api/identity/platform-administrators") return Json<IReadOnlyList<PlatformAdministratorView>>(200, "List platform administrators");
        if (path.StartsWith("/api/identity/platform-administrators/", StringComparison.OrdinalIgnoreCase)) return method == "DELETE" ? NoContent("Revoke platform administrator") : Json<PlatformAdministratorView>(200, "Grant platform administrator");
        if (path.EndsWith("/external-identities/{externalIdentityId}", StringComparison.OrdinalIgnoreCase)) return NoContent("Unlink an external identity");
        if (path.EndsWith("/external-identities", StringComparison.OrdinalIgnoreCase)) return method == "POST" ? Json<ExternalIdentity>(200, "Link an external identity") : Json<IReadOnlyList<ExternalIdentity>>(200, "List external identities");
        if (path == "/api/identity/audit-events") return Json<IReadOnlyList<SecurityAuditEvent>>(200, "List security audit events");
        return null;
    }

    private static OpenApiSuccessResponse? Bootstrap(string method, string path)
    {
        if (path == "/api/bootstrap/profiles" && method == "GET")
            return Json<Agentstration.Web.Hosting.BootstrapManagementView>(200, "List bootstrap profiles and applications");
        if (path == "/api/bootstrap/profiles/preview" && method == "POST")
            return Json<Agentstration.Web.Hosting.BootstrapCompositionPreview>(200, "Preview bootstrap profiles");
        if (path == "/api/bootstrap/applications" && method == "POST")
            return Json<BootstrapApplicationResource>(201, "Apply bootstrap profiles");
        if (path == "/api/bootstrap/applications/{applicationId}" && method == "GET")
            return Json<BootstrapApplicationResource>(200, "Get a bootstrap application");
        return null;
    }

    private static OpenApiSuccessResponse Json<T>(int statusCode, string summary, string? responseDescription = null) =>
        new(statusCode, typeof(T), summary, responseDescription ?? "The operation completed successfully.");

    private static OpenApiSuccessResponse NoContent(string summary, string? responseDescription = null) =>
        new(StatusCodes.Status204NoContent, null, summary, responseDescription ?? "The operation completed successfully and returned no content.");

    private static OpenApiSuccessResponse Binary(int statusCode, string summary) =>
        new(statusCode, typeof(string), summary, "Binary content.", "application/octet-stream");

    private static bool IsModelManagementPath(string path) =>
        path.StartsWith("/api/extensions", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/api/extensionregistrations", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/api/modelproviders", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/api/modelprofiles", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/api/runtimeprofiles", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/api/toolproviders", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/api/tools", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/api/toolexecutionhooks", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/api/vaults", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/api/secrets", StringComparison.OrdinalIgnoreCase);

    private sealed record BootstrapStatusResponse(bool Initialized);
    private sealed record PrincipalIdentifierResponse(Guid PrincipalId);
    private sealed record PlatformRoleResponse(string Role);
    private sealed record RevokedPersonalAccessTokensResponse(int Revoked);
}
