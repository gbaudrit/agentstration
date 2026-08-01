using System.Text.Json;
using System.Text.RegularExpressions;
using Agentstration.Application;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace Agentstration.Evaluation;

public static class ContentWorkflowMetricNames
{
    public const string OutputValid = "content_output_valid";
    public const string SummaryGroundedness = "content_summary_groundedness";
    public const string RequiredFactCoverage = "content_required_fact_coverage";
    public const string CategoryCoverage = "content_category_coverage";
}

public sealed class ContentWorkflowEvaluationContext : EvaluationContext
{
    public ContentWorkflowEvaluationContext(
        string source,
        IReadOnlyList<string> requiredFacts,
        IReadOnlyList<string> expectedCategories)
        : base(
            "content_workflow_expectations",
            JsonSerializer.Serialize(new { source, requiredFacts, expectedCategories }))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        Source = source;
        RequiredFacts = requiredFacts ?? throw new ArgumentNullException(nameof(requiredFacts));
        ExpectedCategories = expectedCategories ?? throw new ArgumentNullException(nameof(expectedCategories));
    }

    public string Source { get; }
    public IReadOnlyList<string> RequiredFacts { get; }
    public IReadOnlyList<string> ExpectedCategories { get; }
}

public sealed partial class ContentWorkflowEvaluator : IEvaluator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> IgnoredWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "de", "des", "du", "et", "for",
        "in", "is", "la", "le", "les", "of", "on", "or", "the", "to", "un", "une", "with"
    };

    private static readonly string[] MetricNames =
    [
        ContentWorkflowMetricNames.OutputValid,
        ContentWorkflowMetricNames.SummaryGroundedness,
        ContentWorkflowMetricNames.RequiredFactCoverage,
        ContentWorkflowMetricNames.CategoryCoverage
    ];

    public IReadOnlyCollection<string> EvaluationMetricNames => MetricNames;

    public ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(modelResponse);

        var context = additionalContext?.OfType<ContentWorkflowEvaluationContext>().SingleOrDefault();
        if (context is null)
        {
            return ValueTask.FromResult(InvalidResult("ContentWorkflowEvaluationContext is required."));
        }

        AgentExecutionResult? output;
        try
        {
            output = JsonSerializer.Deserialize<AgentExecutionResult>(modelResponse.Text, JsonOptions);
        }
        catch (JsonException)
        {
            return ValueTask.FromResult(InvalidResult("The workflow output is not valid JSON."));
        }

        if (output is null || string.IsNullOrWhiteSpace(output.Summary) || output.Categories.Count == 0)
        {
            return ValueTask.FromResult(InvalidResult("The workflow output requires a summary and at least one category."));
        }

        var groundedness = CalculateGroundedness(context.Source, output.Summary);
        var factCoverage = CalculateCoverage(output.Summary, context.RequiredFacts);
        var categoryCoverage = CalculateCategoryCoverage(output.Categories, context.ExpectedCategories);

        EvaluationMetric[] metrics =
        [
            new BooleanMetric(ContentWorkflowMetricNames.OutputValid, true, "The output contains valid JSON, a summary, and categories."),
            new NumericMetric(ContentWorkflowMetricNames.SummaryGroundedness, groundedness, "Share of meaningful summary tokens present in the preserved source."),
            new NumericMetric(ContentWorkflowMetricNames.RequiredFactCoverage, factCoverage, "Share of required facts represented in the summary."),
            new NumericMetric(ContentWorkflowMetricNames.CategoryCoverage, categoryCoverage, "Share of expected categories returned by the workflow.")
        ];

        return ValueTask.FromResult(new EvaluationResult(metrics));
    }

    private static EvaluationResult InvalidResult(string reason)
    {
        EvaluationMetric[] metrics =
        [
            new BooleanMetric(ContentWorkflowMetricNames.OutputValid, false, reason),
            new NumericMetric(ContentWorkflowMetricNames.SummaryGroundedness, 0, reason),
            new NumericMetric(ContentWorkflowMetricNames.RequiredFactCoverage, 0, reason),
            new NumericMetric(ContentWorkflowMetricNames.CategoryCoverage, 0, reason)
        ];
        return new EvaluationResult(metrics);
    }

    private static double CalculateGroundedness(string source, string summary)
    {
        var sourceWords = MeaningfulWords(source).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var summaryWords = MeaningfulWords(summary).ToArray();
        if (summaryWords.Length == 0) return 0;
        return summaryWords.Count(sourceWords.Contains) / (double)summaryWords.Length;
    }

    private static double CalculateCoverage(string summary, IReadOnlyList<string> requiredFacts)
    {
        if (requiredFacts.Count == 0) return 1;
        return requiredFacts.Count(fact => summary.Contains(fact, StringComparison.OrdinalIgnoreCase)) / (double)requiredFacts.Count;
    }

    private static double CalculateCategoryCoverage(IReadOnlyList<string> categories, IReadOnlyList<string> expectedCategories)
    {
        if (expectedCategories.Count == 0) return 1;
        return expectedCategories.Count(expected => categories.Contains(expected, StringComparer.OrdinalIgnoreCase)) / (double)expectedCategories.Count;
    }

    private static IEnumerable<string> MeaningfulWords(string value) =>
        Words().Matches(value)
            .Select(match => match.Value)
            .Where(word => word.Length > 1 && !IgnoredWords.Contains(word));

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}\-'’]*")]
    private static partial Regex Words();
}
