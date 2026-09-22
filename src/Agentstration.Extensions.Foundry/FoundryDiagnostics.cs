using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Agentstration.Aep.Abstractions;
using Microsoft.Extensions.Logging;

namespace Agentstration.Extensions.Foundry;

internal sealed class FoundryDiagnostics(ILogger logger, string operation, string? deployment) : IDisposable
{
    internal static readonly ActivitySource ActivitySource = new("Agentstration.Foundry");
    internal static readonly Meter Meter = new("Agentstration.Foundry");
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("foundry.requests");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("foundry.duration", "ms");
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();
    private readonly Activity? activity = ActivitySource.StartActivity($"foundry.{operation}");
    private readonly string? safeDeployment = deployment is { Length: <= 256 } && !deployment.Any(char.IsControl) ? deployment : null;
    private int? status;
    private int retries;
    private AepUsage? usage;
    private string outcome = "interrupted";

    public void SetStatus(HttpStatusCode responseStatus) => status = (int)responseStatus;
    public void Retried() => retries++;
    public void SetUsage(AepUsage? value) { if (value is not null) usage = value; }
    public void Complete(string value) => outcome = value;

    public void Dispose()
    {
        stopwatch.Stop();
        activity?.SetTag("gen_ai.provider.name", "microsoft-foundry");
        activity?.SetTag("gen_ai.request.model", safeDeployment);
        activity?.SetTag("foundry.operation", operation);
        activity?.SetTag("foundry.outcome", outcome);
        activity?.SetTag("http.response.status_code", status);
        activity?.SetTag("foundry.retry_count", retries);
        activity?.SetTag("gen_ai.usage.input_tokens", usage?.InputTokens);
        activity?.SetTag("gen_ai.usage.output_tokens", usage?.OutputTokens);
        activity?.Dispose();
        var tags = new TagList { { "operation", operation }, { "outcome", outcome } };
        Requests.Add(1, tags);
        Duration.Record(stopwatch.Elapsed.TotalMilliseconds, tags);
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "Foundry {Operation} completed: deployment {Deployment}, outcome {Outcome}, HTTP {HttpStatus}, retries {RetryCount}, duration {DurationMs} ms, input tokens {InputTokens}, output tokens {OutputTokens}, total tokens {TotalTokens}",
                operation, safeDeployment, outcome, status, retries, stopwatch.Elapsed.TotalMilliseconds,
                usage?.InputTokens, usage?.OutputTokens, usage?.TotalTokens);
    }
}
