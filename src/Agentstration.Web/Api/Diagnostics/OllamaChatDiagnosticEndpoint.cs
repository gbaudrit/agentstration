using Agentstration.ModelProviders;
using Agentstration.ModelProviders.Ollama;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Agentstration.Web.Api.Diagnostics;

internal sealed class OllamaChatDiagnosticEndpoint
{
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/diagnostics/models/ollama/chat", HandleAsync);

    private static async Task<IResult> HandleAsync(
        OllamaChatDiagnosticRequest request,
        IModelProviderResolver resolver,
        IOptions<OllamaModelProviderOptions> options,
        ILogger<OllamaChatDiagnosticEndpoint> logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.Prompt)] = ["A prompt is required."]
            });
        }

        var provider = resolver.GetRequiredProvider(OllamaModelProvider.ProviderTypeName);
        var model = options.Value.DefaultModel;
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Running local model diagnostic with provider {ProviderType} and model {Model}", provider.ProviderType, model);
        }

        try
        {
            var messages = new[] { new ChatMessage(ChatRole.User, request.Prompt) };
            var response = await provider.CreateChatClient(model).GetResponseAsync(messages, cancellationToken: cancellationToken);
            return Results.Ok(new OllamaChatDiagnosticResponse(provider.ProviderType, model, response.Text));
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Local model diagnostic failed for provider {ProviderType} and model {Model}", provider.ProviderType, model);
            return Results.Problem("The local Ollama model is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}

internal sealed record OllamaChatDiagnosticRequest(string Prompt);
internal sealed record OllamaChatDiagnosticResponse(string Provider, string Model, string Response);
