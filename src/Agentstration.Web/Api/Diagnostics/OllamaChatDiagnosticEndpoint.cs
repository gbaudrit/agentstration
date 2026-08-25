using Agentstration.ModelProviders;
using Microsoft.Extensions.AI;

namespace Agentstration.Web.Api.Diagnostics;

internal sealed class OllamaChatDiagnosticEndpoint
{
    private const string ProfileResourceId = "reasoning-default";

    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/diagnostics/models/ollama/chat", HandleAsync);

    private static async Task<IResult> HandleAsync(
        OllamaChatDiagnosticRequest request,
        IChatClientResolver resolver,
        IModelProfileStore profiles,
        IModelDeploymentStore deployments,
        IModelProviderConfigurationStore providers,
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

        var profile = await profiles.GetRequiredAsync(ProfileResourceId, cancellationToken);
        var deployment = await deployments.GetRequiredAsync(profile.DeploymentName, cancellationToken);
        var provider = await providers.GetRequiredAsync(deployment.ProviderName, cancellationToken);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Running local model diagnostic with profile {ModelProfile}, deployment {Deployment}, provider {ProviderType}/{ProviderName}, and model {Model}",
                profile.Name,
                deployment.Name,
                provider.ContributionId,
                provider.Name,
                deployment.ModelName);
        }

        try
        {
            var client = await resolver.ResolveAsync(ProfileResourceId, cancellationToken);
            var messages = new[] { new ChatMessage(ChatRole.User, request.Prompt) };
            var response = await client.GetResponseAsync(messages, cancellationToken: cancellationToken);
            return Results.Ok(new OllamaChatDiagnosticResponse(provider.ContributionId, deployment.ModelName, response.Text));
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Local model diagnostic failed for contribution {ContributionId} and model {Model}", provider.ContributionId, deployment.ModelName);
            return Results.Problem("The local Ollama model is unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}

internal sealed record OllamaChatDiagnosticRequest(string Prompt);
internal sealed record OllamaChatDiagnosticResponse(string Provider, string Model, string Response);
