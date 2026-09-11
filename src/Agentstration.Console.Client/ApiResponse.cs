using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentstration.Flow;
using Agentstration.Flow.Contracts;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Contracts;
using Agentstration.Web.Components.Models;
using Agentstration.Work;
using Agentstration.Work.Contracts;

namespace Agentstration.Web.Console;

internal static class ApiResponse
{
    public static async Task<T> ReadAsync<T>(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken) ?? throw new AgentstrationApiException("Agentstration API returned an empty response.", Guid.NewGuid().ToString("N"));
    }

    public static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var correlationId = response.Headers.TryGetValues("X-Correlation-ID", out var values) ? values.FirstOrDefault() : null;
        ApiProblemDetails? problem = null;
        try { problem = await response.Content.ReadFromJsonAsync<ApiProblemDetails>(cancellationToken); }
        catch (HttpRequestException) { }
        catch (System.Text.Json.JsonException) { }
        var message = problem?.Detail ?? problem?.Title ?? $"Agentstration API returned {(int)response.StatusCode} ({response.ReasonPhrase}).";
        throw new AgentstrationApiException(message, correlationId ?? Guid.NewGuid().ToString("N"), response.StatusCode, problem?.Title);
    }

    private sealed record ApiProblemDetails(string? Title, string? Detail, int? Status);
}

public sealed class AgentstrationApiException(string message, string errorId, HttpStatusCode? statusCode = null, string? problemTitle = null) : Exception(message)
{
    public string ErrorId { get; } = errorId;
    public HttpStatusCode? StatusCode { get; } = statusCode;
    public string? ProblemTitle { get; } = problemTitle;
    public bool IsConcurrencyConflict => StatusCode == HttpStatusCode.PreconditionFailed
        || StatusCode == HttpStatusCode.Conflict && string.Equals(ProblemTitle, "Resource version conflict", StringComparison.OrdinalIgnoreCase);
}

