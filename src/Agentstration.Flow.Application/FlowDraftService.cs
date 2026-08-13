using System.Text.Json;
using Agentstration.Flow.Storage.Abstractions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Agentstration.Flow.Application;

public sealed record CreateFlowDraftCommand(string Name, string DisplayName, string? Description, IReadOnlyDictionary<string, string>? Tags, string Template, string UpdatedBy = "local-user");
public sealed record UpdateFlowDraftCommand(string DisplayName, string? Description, IReadOnlyDictionary<string, string>? Tags, FlowGraphDefinition Definition, string UpdatedBy);

public sealed class FlowDraftService(IFlowRepository repository, FlowService flows, IFlowDefinitionValidator validator, TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions IndentedJsonOptions = new(JsonOptions) { WriteIndented = true };
    private static readonly ISerializer YamlSerializer = new SerializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).DisableAliases().Build();
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance).Build();

    public async Task<StoredFlowDraft> CreateAsync(CreateFlowDraftCommand command, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.DisplayName);
        var id = new FlowId(command.Name);
        if (await repository.GetDraftAsync(id, cancellationToken) is not null || await repository.GetAsync(id, cancellationToken) is not null)
            throw new FlowConcurrencyException($"Flow '{id}' already exists.");
        var now = timeProvider.GetUtcNow();
        var graph = FlowDraftTemplates.Create(command.Template);
        var draft = new FlowDraft
        {
            Id = $"{command.Name}-draft",
            FlowId = id,
            DisplayName = command.DisplayName,
            Description = command.Description,
            Tags = Copy(command.Tags),
            Definition = graph,
            CreatedAt = now,
            UpdatedAt = now,
            UpdatedBy = command.UpdatedBy
        };
        FlowValidator.Validate(new FlowResource(id, command.Name, command.Description, "0.1.0", true, null,
            FlowDraftSnapshotAdapter.ToRoutingDefinition(graph), Copy(command.Tags), now, now, command.DisplayName, graph));
        await flows.CreateAsync(new CreateFlowCommand(command.Name, command.Description, "0.1.0", true, FlowDraftSnapshotAdapter.ToRoutingDefinition(graph), command.Tags), cancellationToken);
        return await repository.CreateDraftAsync(draft, cancellationToken);
    }

    public Task<StoredFlowDraft?> GetAsync(FlowId flowId, CancellationToken cancellationToken) => repository.GetDraftAsync(flowId, cancellationToken);

    public async Task<StoredFlowDraft> SaveAsync(FlowId flowId, UpdateFlowDraftCommand command, string expectedETag, CancellationToken cancellationToken)
    {
        var stored = await RequiredAsync(flowId, cancellationToken);
        var updated = stored.Value with
        {
            DisplayName = command.DisplayName,
            Description = command.Description,
            Tags = Copy(command.Tags),
            Definition = command.Definition,
            Revision = stored.Value.Revision + 1,
            UpdatedAt = timeProvider.GetUtcNow(),
            UpdatedBy = command.UpdatedBy
        };
        return await repository.UpdateDraftAsync(updated, expectedETag, cancellationToken);
    }

    public async ValueTask<FlowValidationResult> ValidateAsync(FlowId flowId, CancellationToken cancellationToken)
    {
        var draft = await RequiredAsync(flowId, cancellationToken);
        return await validator.ValidateAsync(draft.Value.Definition, new FlowValidationContext(), cancellationToken);
    }

    public async Task<StoredFlowVersion> PublishAsync(FlowId flowId, string version, string? releaseNotes, bool activate, CancellationToken cancellationToken)
    {
        var draft = await RequiredAsync(flowId, cancellationToken);
        var validation = await validator.ValidateAsync(draft.Value.Definition, new FlowValidationContext(), cancellationToken);
        if (!validation.IsValid) throw new FlowValidationException("flow_validation_failed", "The Flow Draft contains validation errors and cannot be published.");
        var definition = await repository.GetAsync(flowId, cancellationToken) ?? throw new FlowNotFoundException(flowId);
        await flows.UpdateAsync(flowId, new UpdateFlowCommand(draft.Value.Description, version, true, FlowDraftSnapshotAdapter.ToRoutingDefinition(draft.Value.Definition), draft.Value.Tags,
            draft.Value.Definition, draft.Value.DisplayName), definition.ETag, cancellationToken);
        return await flows.PublishVersionAsync(flowId, version, activate, cancellationToken, releaseNotes);
    }

    public async Task<StoredFlowDraft> CreateFromVersionAsync(FlowId flowId, string version, string updatedBy, CancellationToken cancellationToken)
    {
        var published = await repository.GetVersionAsync(flowId, version, cancellationToken) ?? throw new FlowValidationException("flow_version_not_found", $"Flow version '{version}' was not found.");
        if (published.Value.Graph is null) throw new FlowValidationException("flow_version_graph_missing", "This legacy Flow version has no editable graph definition.");
        var current = await RequiredAsync(flowId, cancellationToken);
        var updated = current.Value with { Definition = published.Value.Graph, Revision = current.Value.Revision + 1, UpdatedAt = timeProvider.GetUtcNow(), UpdatedBy = updatedBy };
        return await repository.UpdateDraftAsync(updated, current.ETag, cancellationToken);
    }

    public async Task<string> GetSourceAsync(FlowId flowId, string format, CancellationToken cancellationToken)
    {
        var draft = await RequiredAsync(flowId, cancellationToken);
        return format.Equals("json", StringComparison.OrdinalIgnoreCase)
            ? JsonSerializer.Serialize(draft.Value.Definition, IndentedJsonOptions)
            : ToYaml(draft.Value.Definition);
    }

    public FlowGraphDefinition ParseSource(string source, string format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (source.Length > 1_000_000) throw new FlowValidationException("flow_source_too_large", "Flow source cannot exceed 1 MB.");
        try
        {
            if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
                return JsonSerializer.Deserialize<FlowGraphDefinition>(source, JsonOptions) ?? throw new JsonException("The source is empty.");
            var yamlObject = YamlDeserializer.Deserialize<object>(source);
            var normalized = NormalizeYaml(yamlObject);
            var json = JsonSerializer.Serialize(normalized, JsonOptions);
            return JsonSerializer.Deserialize<FlowGraphDefinition>(json, JsonOptions) ?? throw new JsonException("The source is empty.");
        }
        catch (Exception exception) when (exception is not FlowValidationException)
        {
            throw new FlowValidationException("flow_source_invalid", $"The {format.ToUpperInvariant()} definition is invalid: {exception.Message}");
        }
    }

    public static string ToYaml(FlowGraphDefinition definition)
    {
        var json = JsonSerializer.Serialize(definition, JsonOptions);
        var normalized = NormalizeJson(JsonDocument.Parse(json).RootElement);
        return YamlSerializer.Serialize(normalized);
    }

    private async Task<StoredFlowDraft> RequiredAsync(FlowId flowId, CancellationToken token) => await repository.GetDraftAsync(flowId, token) ?? throw new FlowNotFoundException(flowId);
    private static IReadOnlyDictionary<string, string> Copy(IReadOnlyDictionary<string, string>? source) => source is null ? new Dictionary<string, string>() : new Dictionary<string, string>(source, StringComparer.Ordinal);

    private static object? NormalizeYaml(object? value) => value switch
    {
        IDictionary<object, object> dictionary => dictionary.ToDictionary(item => item.Key.ToString()!, item => NormalizeYaml(item.Value), StringComparer.Ordinal),
        IEnumerable<object> sequence when value is not string => sequence.Select(NormalizeYaml).ToArray(),
        _ => value
    };
    private static object? NormalizeJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(property => property.Name, property => NormalizeJson(property.Value), StringComparer.Ordinal),
        JsonValueKind.Array => value.EnumerateArray().Select(NormalizeJson).ToArray(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };
}

public static class FlowDraftTemplates
{
    private const string SqlAgent = "sql-expert";
    private const string DotNetAgent = "dotnet-expert";

    public static FlowGraphDefinition Create(string template) => template.ToLowerInvariant() switch
    {
        "empty" or "emptyflow" => Empty(),
        "sequential" or "sequentialprocessing" => Sequential(),
        "conditional" or "conditionalflow" => Conditional(),
        _ => AgentRouting()
    };

    private static FlowGraphDefinition AgentRouting()
    {
        var schema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { prompt = new { type = "string" } }, required = new[] { "prompt" } });
        return new FlowGraphDefinition
        {
            EntryStep = "input",
            InputSchema = schema,
            Steps =
            [
                new InputFlowStepDefinition { Name = "input", DisplayName = "Input", Schema = schema },
                new RouterFlowStepDefinition { Name = "route-request", DisplayName = "Route request", Candidates = [new("sql", new(SqlAgent), "SQL questions", ["query", "database"]), new("dotnet", new(DotNetAgent), ".NET questions", ["C#", "ASP.NET"])], Fallback = new(DotNetAgent) },
                new AgentFlowStepDefinition { Name = "execute-agent", DisplayName = "Selected Agent", Agent = new("${steps.route-request.output.selectedAgent}"), InputMapping = JsonSerializer.SerializeToElement(new { prompt = "${input.prompt}" }) },
                new OutputFlowStepDefinition { Name = "complete-flow", DisplayName = "Output", OutputMapping = JsonSerializer.SerializeToElement(new { result = "${steps.execute-agent.output}" }) },
                new FailureFlowStepDefinition { Name = "fail-flow", DisplayName = "Failure" }
            ],
            Transitions =
            [
                new("input-completed", "input", "completed", "route-request"),
                new("router-selected", "route-request", "selected", "execute-agent"),
                new("router-failed", "route-request", "failed", "fail-flow"),
                new("agent-completed", "execute-agent", "completed", "complete-flow"),
                new("agent-failed", "execute-agent", "failed", "fail-flow")
            ],
            Designer = new FlowDesignerMetadata { NodePositions = Positions("input", "route-request", "execute-agent", "complete-flow", "fail-flow") }
        };
    }

    private static FlowGraphDefinition Empty() => new() { EntryStep = "input", Steps = [new InputFlowStepDefinition { Name = "input", DisplayName = "Input" }, new OutputFlowStepDefinition { Name = "output", DisplayName = "Output", OutputMapping = JsonSerializer.SerializeToElement("${input}") }], Transitions = [new("input-output", "input", "completed", "output")], Designer = new() { NodePositions = Positions("input", "output") } };
    private static FlowGraphDefinition Sequential() => Empty();
    private static FlowGraphDefinition Conditional() => AgentRouting();
    private static IReadOnlyDictionary<string, FlowNodePosition> Positions(params string[] names) => names.Select((name, index) => new KeyValuePair<string, FlowNodePosition>(name, new(index < names.Length - 1 ? index * 210 : 630, index == names.Length - 1 ? 190 : 40))).ToDictionary();
}
