using System.ComponentModel;
using Agentstration.Application;
using Agentstration.Application.Ingestion;
using Agentstration.Application.Memory;
using Agentstration.Application.Missions;
using Agentstration.Application.Workspaces;
using Agentstration.Contracts;
using Agentstration.Domain;
using ModelContextProtocol.Server;

namespace Agentstration.Web.Mcp;

[McpServerToolType]
public sealed class PlatformMcpTools(IPlatformStore store, WorkspaceService workspaces, IngestionService ingestion, IMemorySearch memory, MissionService missions)
{
    [McpServerTool(Name = "list_workspaces"), Description("List the available workspaces.")]
    public Task<IReadOnlyList<Workspace>> ListWorkspacesAsync(CancellationToken cancellationToken) => workspaces.ListAsync(cancellationToken);

    [McpServerTool(Name = "list_inboxes"), Description("List inboxes in a workspace.")]
    public Task<IReadOnlyList<Inbox>> ListInboxesAsync(Guid workspaceId, CancellationToken cancellationToken) => workspaces.ListInboxesAsync(new WorkspaceId(workspaceId), cancellationToken);

    [McpServerTool(Name = "ingest_text"), Description("Queue text for durable ingestion and processing.")]
    public Task<Application.Common.Result<IngestItemResponse>> IngestTextAsync(Guid workspaceId, Guid inboxId, string text, CancellationToken cancellationToken) => ingestion.IngestAsync(new WorkspaceId(workspaceId), new InboxId(inboxId), text, null, null, "text/plain", cancellationToken);

    [McpServerTool(Name = "ingest_url"), Description("Fetch and queue an HTTP or HTTPS URL.")]
    public Task<Application.Common.Result<IngestItemResponse>> IngestUrlAsync(Guid workspaceId, Guid inboxId, string url, CancellationToken cancellationToken) => ingestion.IngestAsync(new WorkspaceId(workspaceId), new InboxId(inboxId), null, url, null, "text/html", cancellationToken);

    [McpServerTool(Name = "search_memory"), Description("Search workspace memory.")]
    public Task<IReadOnlyList<MemoryEntry>> SearchMemoryAsync(Guid workspaceId, string query, int limit, CancellationToken cancellationToken) => memory.SearchAsync(new WorkspaceId(workspaceId), query, limit, cancellationToken);

    [McpServerTool(Name = "create_mission"), Description("Create a deterministic monitoring mission.")]
    public Task<Application.Common.Result<Mission>> CreateMissionAsync(Guid workspaceId, string name, string objective, string sourceUrl, int frequencyMinutes, decimal? threshold, CancellationToken cancellationToken) => missions.CreateAsync(new WorkspaceId(workspaceId), new CreateMissionRequest(name, objective, sourceUrl, frequencyMinutes, threshold), cancellationToken);

    [McpServerTool(Name = "get_mission"), Description("Get a mission and its history.")]
    public Task<Application.Common.Result<MissionDetails>> GetMissionAsync(Guid workspaceId, Guid missionId, CancellationToken cancellationToken) => missions.GetAsync(new WorkspaceId(workspaceId), new MissionId(missionId), cancellationToken);

    [McpServerTool(Name = "list_mission_runs"), Description("List mission executions.")]
    public Task<IReadOnlyList<MissionRun>> ListMissionRunsAsync(Guid workspaceId, Guid missionId, CancellationToken cancellationToken) => store.ListMissionRunsAsync(new WorkspaceId(workspaceId), new MissionId(missionId), cancellationToken);

    [McpServerTool(Name = "run_mission_now"), Description("Run a mission immediately.")]
    public Task<Application.Common.Result<MissionRun>> RunMissionNowAsync(Guid workspaceId, Guid missionId, CancellationToken cancellationToken) => missions.RunAsync(new WorkspaceId(workspaceId), new MissionId(missionId), cancellationToken);
}
