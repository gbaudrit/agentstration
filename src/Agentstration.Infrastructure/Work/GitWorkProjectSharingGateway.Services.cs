using System.Text.Json;
using Agentstration.Resources;
using Agentstration.Secrets.Abstractions;
using Agentstration.Tools;
using Agentstration.Tools.Mcp;
using Agentstration.Work;

namespace Agentstration.Infrastructure.Work;

public sealed partial class GitWorkProjectSharingGateway
{
    private async Task<Result<Route>> ResolveServiceGitAsync(WorkspaceId workspace, Guid principal, WorkProjectResolvedBinding connection,
        ToolResource tool, string? requestedRef, IReadOnlyList<string> requestedPaths, CancellationToken token)
    {
        Result<Route> Failure() => Result<Route>.Failure("sharing_service_incompatible");
        if (connection.NativeRoute is not { } route || !WorkProjectConnectionRules.Evidence(connection)) return Failure();
        var options = await services.NativeOptionsAsync(connection, token);
        if (!options.TryGetProperty("repository", out var repository) || repository.ValueKind != JsonValueKind.String
            || !options.TryGetProperty("authentication", out var authentication) || authentication.GetString() is not ("Basic" or "Bearer")
            || !options.TryGetProperty("allowedRefPrefixes", out var refs) || refs.ValueKind != JsonValueKind.Array
            || !options.TryGetProperty("allowedPathPrefixes", out var paths) || paths.ValueKind != JsonValueKind.Array) return Failure();
        var boundRef = options.TryGetProperty("refName", out var reference) ? reference.GetString() : null;
        var refName = requestedRef ?? boundRef;
        if (refName is not { Length: > 0 and <= 256 } || !refName.StartsWith("refs/heads/", StringComparison.Ordinal)
            || boundRef is not null && boundRef != refName || string.IsNullOrWhiteSpace(repository.GetString())
            || !refs.EnumerateArray().Any(value => value.GetString() is { Length: > 0 } prefix && refName.StartsWith(prefix, StringComparison.Ordinal))
            || requestedPaths.Count is < 1 or > 32 || requestedPaths.Any(path => !paths.EnumerateArray().Any(value => value.GetString() is { Length: > 0 } prefix
                && (path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal))))) return Failure();
        var provider = (await resources.GetExactAsync<ToolProviderResource>(route.Provider.Address, token))?.Value;
        if (provider is null || provider.Uid != route.Provider.Uid || provider.Generation != route.Provider.Generation) return Failure();
        await RevalidateServiceAsync(workspace, principal, connection, token);
        return Result<Route>.Success(new(tool, provider, refName, repository.GetString()!, connection));
    }
    private async Task RevalidateServiceAsync(WorkspaceId workspace, Guid principal, WorkProjectResolvedBinding captured, CancellationToken token)
    {
        var currentBinding = await bindings.ResolveAsync(workspace, principal, captured.Binding, token);
        if (!currentBinding.IsSuccess || WorkPlanDefinition.Digest(currentBinding.Value) != WorkPlanDefinition.Digest(captured))
            throw new ToolResolutionException("work_project_connection_changed", "The retained Git service is no longer available.");
    }
    private async Task<ResolvedSecretInputs> ResolveServiceHeadersAsync(WorkspaceId workspace, Guid principal, WorkProjectResolvedBinding captured, CancellationToken token)
    {
        await RevalidateServiceAsync(workspace, principal, captured, token);
        return await services.ResolveNativeHeadersAsync(workspace, captured, token);
    }
}
