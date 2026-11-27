using System.Text.Json;
using Agentstration.Afc.Abstractions;
using Agentstration.Afc.Server;
using Agentstration.Atc.Abstractions;
using Agentstration.Atc.Server;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agentstration.Extensions.GitHub;

public sealed class GitHubContractMcpServer(GitHubConnection connections, GitHubIssueOperations issues, GitHubCodingOperations coding,
    IHttpContextAccessor http, IEnumerable<McpServerTool> nativeTools, TimeProvider clock)
{
    public ValueTask<ListToolsResult> ListAsync(RequestContext<ListToolsRequestParams> request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var tools = nativeTools.Select(tool => tool.ProtocolTool).ToList();
        tools.AddRange(AtcContract.Tools.Select(operation => new Tool
        {
            Name = "github_atc_" + operation,
            InputSchema = AtcSchemas.Input(operation),
            OutputSchema = AtcSchemas.Output(operation),
            Description = "Governed GitHub tracking " + operation
        }));
        tools.AddRange(AfcContract.Tools.Select(operation => new Tool
        {
            Name = "github_afc_" + operation,
            InputSchema = AfcSchemas.Input(operation),
            OutputSchema = AfcSchemas.Output(operation),
            Description = "Governed GitHub forge " + operation
        }));
        return ValueTask.FromResult(new ListToolsResult { Tools = tools });
    }
    public async ValueTask<CallToolResult> CallAsync(RequestContext<CallToolRequestParams> request, CancellationToken token)
    {
        var name = request.Params?.Name ?? "";
        if (nativeTools.SingleOrDefault(tool => tool.ProtocolTool.Name == name) is { } native) return await native.InvokeAsync(request, token);
        var tracking = name.StartsWith("github_atc_", StringComparison.Ordinal);
        var requestId = "unknown";
        try
        {
            if (!tracking && !name.StartsWith("github_afc_", StringComparison.Ordinal)) throw new GitHubProviderException("unsupported_operation");
            if (request.Params?.Arguments?.TryGetValue("arguments", out var arguments) == true && arguments.TryGetProperty("requestId", out var id) && id.ValueKind == JsonValueKind.String)
                requestId = id.GetString()!;
            var configuration = GitHubServiceContract.Read(http.HttpContext?.Request ?? throw new GitHubProviderException("github_service_required"));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            var session = new GitHubContractSession(configuration, connections, issues, coding);
            await session.AuthorizeAsync(linked.Token);
            var target = session.Target;
            if (tracking)
            {
                var provider = new GitHubTrackingProvider(session);
                var server = new AtcMcpServer([new(target, provider, (_, _) => ValueTask.FromResult(
                    new AtcAccessContext(target, session.Subject, target.Revision, provider.Capabilities.Operations, [])))], "github_atc", clock);
                return await server.CallToolAsync(request, linked.Token);
            }
            else
            {
                var provider = new GitHubForgeProvider(session); var forge = new AfcTargetRef(target.ProviderId, target.TargetId, target.BindingId, target.Revision);
                var server = new AfcMcpServer([new(forge, provider, (_, _) => ValueTask.FromResult(
                    new AfcAccessContext(forge, session.Subject, forge.Revision, provider.Capabilities.Operations)))], "github_afc", clock);
                return await server.CallToolAsync(request, linked.Token);
            }
        }
        catch (Exception error) when (error is GitHubProviderException or JsonException or InvalidOperationException or KeyNotFoundException or HttpRequestException or OperationCanceledException)
        {
            var code = error is GitHubProviderException github ? github.Code : error is OperationCanceledException ? "cancelled" : "github_provider_unavailable";
            return new CallToolResult
            {
                IsError = true,
                StructuredContent = tracking
                ? JsonSerializer.SerializeToElement(new AtcReply<object>(AtcContract.Id, AtcContract.Version, requestId, Error: new(code)), AtcContract.Json)
                : JsonSerializer.SerializeToElement(new AfcReply<object>(AfcContract.Id, AfcContract.Version, requestId, Error: new(code)), AfcContract.Json)
            };
        }
    }
}
