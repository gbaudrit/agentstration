using System.Text.Json;
using Agentstration.Application;
using Agentstration.Application.Ingestion;
using Agentstration.Application.Memory;
using Agentstration.Application.Routing;
using Agentstration.Application.Workflows;
using Agentstration.Application.Workspaces;
using Agentstration.Contracts;
using Agentstration.Domain;
using Agentstration.Evaluation;
using Agentstration.Infrastructure.Agents;
using Agentstration.Infrastructure.Persistence;
using Agentstration.Runtime.Local;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace Agentstration.Evaluation.Tests;

[TestClass]
public sealed class ContentWorkflowEvaluationTests
{
    private const double MinimumGroundedness = 0.95;
    private const double MinimumFactCoverage = 1.0;
    private const double MinimumCategoryCoverage = 1.0;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task DeterministicContentWorkflowMeetsQualityThresholds()
    {
        var dataSet = await LoadDataSetAsync(default);
        Assert.AreEqual("1.0", dataSet.Version);

        foreach (var evaluationCase in dataSet.Cases)
        {
            var output = await ExecuteWorkflowAsync(evaluationCase.Source, default);
            var responseJson = JsonSerializer.Serialize(output, JsonOptions);
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, responseJson));
            var messages = new[] { new ChatMessage(ChatRole.User, evaluationCase.Source) };
            var context = new ContentWorkflowEvaluationContext(
                evaluationCase.Source,
                evaluationCase.RequiredFacts,
                evaluationCase.ExpectedCategories);

            var result = await new ContentWorkflowEvaluator().EvaluateAsync(
                messages,
                response,
                additionalContext: [context],
                cancellationToken: default);

            Assert.AreEqual(true, result.Get<BooleanMetric>(ContentWorkflowMetricNames.OutputValid).Value, evaluationCase.Name);
            Assert.IsGreaterThanOrEqualTo(
                MinimumGroundedness,
                result.Get<NumericMetric>(ContentWorkflowMetricNames.SummaryGroundedness).Value ?? 0,
                evaluationCase.Name);
            Assert.IsGreaterThanOrEqualTo(
                MinimumFactCoverage,
                result.Get<NumericMetric>(ContentWorkflowMetricNames.RequiredFactCoverage).Value ?? 0,
                evaluationCase.Name);
            Assert.IsGreaterThanOrEqualTo(
                MinimumCategoryCoverage,
                result.Get<NumericMetric>(ContentWorkflowMetricNames.CategoryCoverage).Value ?? 0,
                evaluationCase.Name);
        }
    }

    [TestMethod]
    public async Task EvaluatorRejectsMalformedWorkflowOutput()
    {
        var evaluationCase = (await LoadDataSetAsync(default)).Cases[0];
        var context = new ContentWorkflowEvaluationContext(
            evaluationCase.Source,
            evaluationCase.RequiredFacts,
            evaluationCase.ExpectedCategories);
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "not-json"));

        var result = await new ContentWorkflowEvaluator().EvaluateAsync(
            [new ChatMessage(ChatRole.User, evaluationCase.Source)],
            response,
            additionalContext: [context],
            cancellationToken: default);

        Assert.AreEqual(false, result.Get<BooleanMetric>(ContentWorkflowMetricNames.OutputValid).Value);
        Assert.AreEqual(0, result.Get<NumericMetric>(ContentWorkflowMetricNames.SummaryGroundedness).Value);
    }

    private static async Task<AgentExecutionResult> ExecuteWorkflowAsync(string source, CancellationToken cancellationToken)
    {
        var store = new InMemoryPlatformStore();
        var eventBus = new NoOpEventBus();
        var workspaces = new WorkspaceService(store, TimeProvider.System);
        var ingestion = new IngestionService(store, eventBus, new NoOpContentSourceReader(), TimeProvider.System);
        var memory = new MemoryService(store);
        var runtime = new MicrosoftExtensionsAiAgentRuntime(new SingleChatClientResolver(new DeterministicChatClient()));
        var workflow = new ContentProcessingWorkflow(store, new DeterministicIntentRouter(), runtime, memory, eventBus, TimeProvider.System);
        var workspace = (await workspaces.CreateAsync("Evaluation workspace", cancellationToken)).Value!;
        var inbox = (await workspaces.CreateInboxAsync(workspace.Id, new CreateInboxRequest("Evaluation inbox", null, null), cancellationToken)).Value!.Inbox;
        var accepted = (await ingestion.IngestAsync(workspace.Id, inbox.Id, source, null, null, "text/plain", cancellationToken)).Value!;

        await workflow.ExecuteAsync(workspace.Id, accepted.ItemId, cancellationToken);

        var item = (await ingestion.GetAsync(workspace.Id, accepted.ItemId, cancellationToken)).Value!;
        Assert.AreEqual(source, item.Raw.Value, "Evaluation must run without changing the preserved source.");
        Assert.HasCount(1, item.Memory);
        return new AgentExecutionResult(item.Memory[0].Content, item.Memory[0].Categories);
    }

    private static async Task<ContentWorkflowEvaluationDataSet> LoadDataSetAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "content-workflow-cases.json");
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ContentWorkflowEvaluationDataSet>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("The content workflow evaluation dataset is invalid.");
    }

    private sealed class NoOpEventBus : IEventBus
    {
        public Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken cancellationToken) where TEvent : IDomainEvent => Task.CompletedTask;
    }

    private sealed class NoOpContentSourceReader : IContentSourceReader
    {
        public Task<string> ReadUrlAsync(Uri uri, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

public sealed record ContentWorkflowEvaluationDataSet(string Version, IReadOnlyList<ContentWorkflowEvaluationCase> Cases);

public sealed record ContentWorkflowEvaluationCase(
    string Name,
    string Source,
    IReadOnlyList<string> RequiredFacts,
    IReadOnlyList<string> ExpectedCategories);
