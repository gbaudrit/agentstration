using System.Diagnostics;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Agentstration.Aep.Validation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Agentstration.Aep.Inspector;

public sealed record AepInspectorSnapshot(
    Uri Endpoint,
    AepManifest Manifest,
    AepHealth Health,
    AepValidationResult Validation,
    string RawManifest,
    DateTimeOffset ConnectedAt);

public sealed record AepProviderInspection(
    AepModelProviderDescriptor Provider,
    AepProviderHealth Health,
    IReadOnlyList<AepModelDescriptor> Models);

public sealed record AepChatResult(string Text, string RawResponse, TimeSpan Duration, string Model, bool Streaming);

public sealed record AepInspectorTool(
    string Id,
    string Name,
    string? Description,
    string Server,
    string NativeName,
    JsonElement InputSchema,
    JsonElement? OutputSchema);

public sealed record AepToolInvocationResult(string ToolId, string RawResult, TimeSpan Duration);

public sealed class InspectorSession(ILoggerFactory loggerFactory, Func<HttpMessageHandler>? handlerFactory = null) : IAepHttpTraceSink, IAsyncDisposable
{
    private static readonly JsonSerializerOptions PrettyJson = new(AepProtocol.JsonOptions) { WriteIndented = true };
    private readonly List<AepHttpTrace> traces = [];
    private readonly List<Uri> recentEndpoints = [];
    private readonly Dictionary<string, (McpClient Client, McpClientTool Tool)> toolBindings = new(StringComparer.Ordinal);
    private readonly List<McpClient> mcpClients = [];
    private HttpClient? httpClient;
    private AepClient? client;
    public AepInspectorSnapshot? Snapshot { get; private set; }
    public IReadOnlyList<AepHttpTrace> Traces => traces;
    public IReadOnlyList<Uri> RecentEndpoints => recentEndpoints;
    public IReadOnlyList<AepModelProviderDescriptor> ModelProviders { get; private set; } = [];
    public IReadOnlyList<AepInspectorTool> Tools { get; private set; } = [];
    public event Action? Changed;

    public async Task<AepInspectorSnapshot> ConnectAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        var uri = ValidateEndpoint(endpoint);
        await ResetConnectionAsync();
        traces.Clear();
        var handler = new AepTracingHandler(this) { InnerHandler = CreateHandler() };
        httpClient = new HttpClient(handler) { BaseAddress = new Uri(uri.AbsoluteUri.TrimEnd('/') + '/'), Timeout = TimeSpan.FromSeconds(90) };
        client = new AepClient(httpClient);
        var manifest = await client.GetManifestAsync(cancellationToken);
        var health = await client.GetHealthAsync(cancellationToken);
        var validation = await new AepValidator().ValidateAsync(client, cancellationToken);
        ModelProviders = manifest.Capabilities.ContainsKey(AepCapabilityNames.ModelProvider)
            ? await client.ListModelProvidersAsync(cancellationToken)
            : [];
        Snapshot = new(uri, manifest, health, validation, Pretty(manifest), DateTimeOffset.UtcNow);
        recentEndpoints.RemoveAll(value => value == uri);
        recentEndpoints.Insert(0, uri);
        if (recentEndpoints.Count > 5) recentEndpoints.RemoveRange(5, recentEndpoints.Count - 5);
        return Snapshot;
    }

    public async Task<AepHealth> RefreshHealthAsync(CancellationToken cancellationToken = default)
    {
        var activeClient = RequiredClient();
        var health = await activeClient.GetHealthAsync(cancellationToken);
        if (Snapshot is not null) Snapshot = Snapshot with { Health = health };
        return health;
    }

    public async Task<AepValidationResult> ValidateAsync(CancellationToken cancellationToken = default)
    {
        var result = await new AepValidator().ValidateAsync(RequiredClient(), cancellationToken);
        if (Snapshot is not null) Snapshot = Snapshot with { Validation = result };
        return result;
    }

    public async Task<AepProviderInspection> InspectProviderAsync(string providerId, CancellationToken cancellationToken = default)
    {
        var descriptor = ModelProviders.FirstOrDefault(value => value.Id == providerId)
            ?? throw new InvalidOperationException($"Model provider '{providerId}' is not present in the manifest.");
        var provider = RequiredClient().CreateModelProvider(providerId);
        var health = await provider.GetHealthAsync(cancellationToken);
        var models = string.Equals(health.Status, "available", StringComparison.OrdinalIgnoreCase)
            ? await provider.ListModelsAsync(cancellationToken)
            : [];
        return new(descriptor, health, models);
    }

    public async Task<AepChatResult> ChatAsync(
        string providerId,
        string model,
        string prompt,
        string? systemPrompt,
        float? temperature,
        int? maxOutputTokens,
        bool streaming,
        Func<string, Task>? onUpdate = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("A model is required.", nameof(model));
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("A prompt is required.", nameof(prompt));
        var messages = new List<AepMessage>();
        if (!string.IsNullOrWhiteSpace(systemPrompt)) messages.Add(new(AepRole.System, [AepContent.FromText(systemPrompt)]));
        messages.Add(new(AepRole.User, [AepContent.FromText(prompt)]));
        var request = new AepChatRequest(model, messages, new AepModelOptions { Temperature = temperature, MaxOutputTokens = maxOutputTokens });
        var provider = RequiredClient().CreateModelProvider(providerId);
        var stopwatch = Stopwatch.StartNew();
        if (!streaming)
        {
            var response = await provider.ChatAsync(request, cancellationToken);
            stopwatch.Stop();
            return new(Text(response.Messages.SelectMany(value => value.Contents)), Pretty(response), stopwatch.Elapsed, response.Model ?? model, false);
        }
        var text = new List<string>();
        var updates = new List<AepChatUpdate>();
        await foreach (var update in provider.ChatStreamingAsync(request, cancellationToken).WithCancellation(cancellationToken))
        {
            updates.Add(update);
            var fragment = Text(update.Contents);
            if (fragment.Length == 0) continue;
            text.Add(fragment);
            if (onUpdate is not null) await onUpdate(string.Concat(text));
        }
        stopwatch.Stop();
        return new(string.Concat(text), Pretty(updates), stopwatch.Elapsed, model, true);
    }

    public async Task<IReadOnlyList<AepInspectorTool>> LoadToolsAsync(CancellationToken cancellationToken = default)
    {
        if (Snapshot is null) throw new InvalidOperationException("Connect to an extension first.");
        await DisposeMcpClientsAsync();
        var result = new List<AepInspectorTool>();
        foreach (var server in Snapshot.Manifest.Mcp?.Servers ?? [])
        {
            var endpoint = AepDescriptorValidator.ResolveMcpEndpoint(Snapshot.Endpoint, server);
            var handler = new AepTracingHandler(this) { InnerHandler = CreateHandler() };
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions { Endpoint = endpoint, Name = server.Id },
                new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) },
                loggerFactory,
                ownsHttpClient: true);
            var mcp = await McpClient.CreateAsync(transport, loggerFactory: loggerFactory, cancellationToken: cancellationToken);
            mcpClients.Add(mcp);
            var nativeTools = await mcp.ListToolsAsync(cancellationToken: cancellationToken);
            foreach (var contribution in (Snapshot.Manifest.Contributions.Tools ?? []).Where(value => value.Mcp.Server == server.Id))
            {
                var native = nativeTools.FirstOrDefault(value => value.ProtocolTool.Name == contribution.Mcp.Tool)
                    ?? throw new InvalidOperationException($"Contribution '{contribution.Id}' maps to missing MCP tool '{contribution.Mcp.Tool}'.");
                result.Add(new(contribution.Id, contribution.DisplayName, contribution.Description ?? native.Description, server.Id, native.Name, native.JsonSchema, native.ReturnJsonSchema));
                toolBindings[contribution.Id] = (mcp, native);
            }
        }
        Tools = result;
        return Tools;
    }

    public async Task<AepToolInvocationResult> InvokeToolAsync(string toolId, string argumentsJson, CancellationToken cancellationToken = default)
    {
        if (!toolBindings.TryGetValue(toolId, out var binding)) throw new InvalidOperationException($"Tool '{toolId}' has not been loaded.");
        using var arguments = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        if (arguments.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Tool arguments must be a JSON object.", nameof(argumentsJson));
        var values = arguments.RootElement.EnumerateObject().ToDictionary(value => value.Name, value => (object?)value.Value.Clone(), StringComparer.Ordinal);
        var stopwatch = Stopwatch.StartNew();
        var response = await binding.Tool.InvokeAsync(new AIFunctionArguments(values), cancellationToken);
        stopwatch.Stop();
        return new(toolId, Pretty(response), stopwatch.Elapsed);
    }

    public void ClearTraces()
    {
        traces.Clear();
        Changed?.Invoke();
    }

    public ValueTask RecordAsync(AepHttpTrace trace, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        traces.Add(trace);
        if (traces.Count > 200) traces.RemoveRange(0, traces.Count - 200);
        Changed?.Invoke();
        return ValueTask.CompletedTask;
    }

    private AepClient RequiredClient() => client ?? throw new InvalidOperationException("Connect to an extension first.");
    private HttpMessageHandler CreateHandler() => handlerFactory?.Invoke() ?? new HttpClientHandler();
    private static Uri ValidateEndpoint(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo)
            ? new Uri(uri.AbsoluteUri.TrimEnd('/') + '/')
            : throw new ArgumentException("Endpoint must be an absolute HTTP(S) URL without embedded credentials.", nameof(endpoint));
    private static string Text(IEnumerable<AepContent> contents) => string.Concat(contents.Where(value => value.Kind == AepContentKind.Text).Select(value => value.Text));
    private static string Pretty<T>(T value) => JsonSerializer.Serialize(value, PrettyJson);

    private async Task ResetConnectionAsync()
    {
        await DisposeMcpClientsAsync();
        httpClient?.Dispose();
        httpClient = null;
        client = null;
        Snapshot = null;
        ModelProviders = [];
    }

    private async Task DisposeMcpClientsAsync()
    {
        toolBindings.Clear();
        Tools = [];
        foreach (var mcp in mcpClients) await mcp.DisposeAsync();
        mcpClients.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await ResetConnectionAsync();
        GC.SuppressFinalize(this);
    }
}
