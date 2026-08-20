using Agentstration.Application;
using Agentstration.Application.Common;
using Agentstration.Application.Ingestion;
using Agentstration.Application.Missions;
using Agentstration.Application.Workspaces;
using Agentstration.Contracts;
using Agentstration.Domain;

namespace Agentstration.Web;

public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapAgentstrationApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api");
        api.MapPost("/workspaces", CreateWorkspaceAsync);
        api.MapGet("/workspaces", ListWorkspacesAsync);
        api.MapPost("/workspaces/{workspaceId:guid}/inboxes", CreateInboxAsync);
        api.MapGet("/workspaces/{workspaceId:guid}/inboxes", (Guid workspaceId, WorkspaceService service, CancellationToken token) => service.ListInboxesAsync(new WorkspaceId(workspaceId), token));
        api.MapPost("/workspaces/{workspaceId:guid}/inboxes/{inboxId:guid}/items", IngestAsync).DisableAntiforgery();
        api.MapGet("/workspaces/{workspaceId:guid}/items/{itemId:guid}", GetItemAsync);
        api.MapPost("/workspaces/{workspaceId:guid}/missions", CreateMissionAsync);
        api.MapGet("/workspaces/{workspaceId:guid}/missions", (Guid workspaceId, MissionService service, CancellationToken token) => service.ListAsync(new WorkspaceId(workspaceId), token));
        api.MapGet("/workspaces/{workspaceId:guid}/missions/{missionId:guid}", GetMissionAsync);
        api.MapPost("/workspaces/{workspaceId:guid}/missions/{missionId:guid}/run", RunMissionAsync);
        api.MapGet("/workspaces/{workspaceId:guid}/missions/{missionId:guid}/runs", async (Guid workspaceId, Guid missionId, IPlatformStore store, CancellationToken token) => await store.ListMissionRunsAsync(new WorkspaceId(workspaceId), new MissionId(missionId), token));
        return endpoints;
    }

    private static async Task<IResult> ListWorkspacesAsync(
        WorkspaceService legacyService,
        CancellationToken token)
    {
        return Results.Ok(await legacyService.ListAsync(token));
    }

    private static async Task<IResult> CreateWorkspaceAsync(CreateWorkspaceRequest request, WorkspaceService service, CancellationToken token) => ToHttp(await service.CreateAsync(request.Name, token), value => Results.Created($"/api/workspaces/{value.Id}", value));
    private static async Task<IResult> CreateInboxAsync(Guid workspaceId, CreateInboxRequest request, WorkspaceService service, CancellationToken token) => ToHttp(await service.CreateInboxAsync(new WorkspaceId(workspaceId), request, token), value => Results.Created($"/api/workspaces/{workspaceId}/inboxes/{value.Inbox.Id}", value));

    private static async Task<IResult> IngestAsync(Guid workspaceId, Guid inboxId, HttpRequest request, IngestionService service, CancellationToken token)
    {
        string? text = null, url = null, externalId = null;
        var mediaType = request.ContentType ?? "text/plain";
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(token);
            text = form["text"].FirstOrDefault(); url = form["url"].FirstOrDefault(); externalId = form["externalId"].FirstOrDefault();
            var file = form.Files.FirstOrDefault();
            if (file is not null)
            {
                var allowed = new[] { "text/plain", "text/markdown", "application/json" };
                if (!allowed.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase)) return Results.Problem(statusCode: 415, title: "Unsupported file type", detail: "Allowed types: text/plain, text/markdown, application/json.");
                if (file.Length > 2 * 1024 * 1024) return Results.Problem(statusCode: 413, title: "Payload too large");
                using var reader = new StreamReader(file.OpenReadStream());
                text = await reader.ReadToEndAsync(token); mediaType = file.ContentType;
            }
        }
        else if (mediaType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            var body = await request.ReadFromJsonAsync<IngestItemRequest>(cancellationToken: token);
            text = body?.Text; url = body?.Url; externalId = body?.ExternalId;
        }
        else
        {
            using var reader = new StreamReader(request.Body);
            text = await reader.ReadToEndAsync(token);
        }

        var result = await service.IngestAsync(new WorkspaceId(workspaceId), new InboxId(inboxId), text, url, externalId, mediaType, token);
        return ToHttp(result, value => Results.Accepted($"/api/workspaces/{workspaceId}/items/{value.ItemId}", value));
    }

    private static async Task<IResult> GetItemAsync(Guid workspaceId, Guid itemId, IngestionService service, CancellationToken token) => ToHttp(await service.GetAsync(new WorkspaceId(workspaceId), new ItemId(itemId), token), Results.Ok);
    private static async Task<IResult> CreateMissionAsync(Guid workspaceId, CreateMissionRequest request, MissionService service, CancellationToken token) => ToHttp(await service.CreateAsync(new WorkspaceId(workspaceId), request, token), value => Results.Created($"/api/workspaces/{workspaceId}/missions/{value.Id}", value));
    private static async Task<IResult> GetMissionAsync(Guid workspaceId, Guid missionId, MissionService service, CancellationToken token) => ToHttp(await service.GetAsync(new WorkspaceId(workspaceId), new MissionId(missionId), token), Results.Ok);
    private static async Task<IResult> RunMissionAsync(Guid workspaceId, Guid missionId, MissionService service, CancellationToken token) => ToHttp(await service.RunAsync(new WorkspaceId(workspaceId), new MissionId(missionId), token), Results.Ok);

    private static IResult ToHttp<T>(Result<T> result, Func<T, IResult> success) => result.IsSuccess
        ? success(result.Value!)
        : Results.Problem(statusCode: result.Error!.Code.EndsWith("not_found", StringComparison.Ordinal) ? 404 : 400, title: result.Error.Code, detail: result.Error.Message);
}
