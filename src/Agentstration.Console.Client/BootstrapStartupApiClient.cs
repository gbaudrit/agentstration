using System.Net.Http.Json;
using Agentstration.Bootstrap.Contracts;
using Agentstration.Resources;

namespace Agentstration.Web.Console;

public interface IBootstrapStartupApiClient
{
    Task<BootstrapStartupAdmission> GetAdmissionAsync(CancellationToken cancellationToken);
    Task<BootstrapStartupSnapshot> GetAsync(CancellationToken cancellationToken);
    Task<BootstrapStartupResourcePage> GetResourcesAsync(BootstrapStartupResourceQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(new BootstrapStartupResourcePage(query.AttemptId, 0, 0, query.Offset, query.Limit, []));
    Task<BootstrapStartupModelOptions> GetModelOptionsAsync(Guid attemptId, string resourceId, string? providerName, string? providerNamespace, CancellationToken cancellationToken) =>
        Task.FromException<BootstrapStartupModelOptions>(new NotSupportedException());
    Task ApplyModelChangeAsync(string resourceId, BootstrapStartupModelChange request, CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException());
}

public sealed class BootstrapStartupApiClient(HttpClient client) : IBootstrapStartupApiClient
{
    public Task<BootstrapStartupAdmission> GetAdmissionAsync(CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<BootstrapStartupAdmission>(client, "api/bootstrap/startup/admission", cancellationToken);
    public Task<BootstrapStartupSnapshot> GetAsync(CancellationToken cancellationToken) =>
        ApiResponse.ReadAsync<BootstrapStartupSnapshot>(client, "api/bootstrap/startup", cancellationToken);
    public Task<BootstrapStartupResourcePage> GetResourcesAsync(BootstrapStartupResourceQuery query, CancellationToken cancellationToken)
    {
        var url = $"api/bootstrap/startup/resources?attemptId={query.AttemptId:D}&offset={query.Offset}&limit={query.Limit}";
        foreach (var (key, value) in new[] { ("stepId", query.StepId), ("kind", query.Kind), ("groupId", query.GroupId), ("state", query.State), ("search", query.Search) })
            if (!string.IsNullOrWhiteSpace(value)) url += $"&{key}={Uri.EscapeDataString(value)}";
        return ApiResponse.ReadAsync<BootstrapStartupResourcePage>(client, url, cancellationToken);
    }
    public Task<BootstrapStartupModelOptions> GetModelOptionsAsync(Guid attemptId, string resourceId, string? providerName, string? providerNamespace, CancellationToken cancellationToken)
    {
        var url = $"api/bootstrap/startup/resources/{Uri.EscapeDataString(resourceId)}/model-options?attemptId={attemptId:D}";
        if (!string.IsNullOrWhiteSpace(providerName)) url += $"&providerName={Uri.EscapeDataString(providerName)}&providerNamespace={Uri.EscapeDataString(providerNamespace ?? "default")}";
        return ApiResponse.ReadAsync<BootstrapStartupModelOptions>(client, url, cancellationToken);
    }
    public async Task ApplyModelChangeAsync(string resourceId, BootstrapStartupModelChange request, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync($"api/bootstrap/startup/resources/{Uri.EscapeDataString(resourceId)}/model-substitution", request, cancellationToken);
        await ApiResponse.EnsureSuccessAsync(response, cancellationToken);
    }
}

public static class BootstrapStartupNavigation
{
    public const string ProgressPath = "/startup/progress";
    public static string LocalReturnUrl(string? value) => string.IsNullOrWhiteSpace(value) || !value.StartsWith('/')
        || value.StartsWith("//", StringComparison.Ordinal) || value.Contains('\\') || value.Any(char.IsControl)
        || value.StartsWith("/startup", StringComparison.OrdinalIgnoreCase) || value.StartsWith("/login", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("/logout", StringComparison.OrdinalIgnoreCase) ? "/" : value;
    public static string ProgressUrl(string? returnUrl)
    {
        var local = LocalReturnUrl(returnUrl);
        var url = ProgressPath + "?ReturnUrl=" + Uri.EscapeDataString(local);
        var question = local.IndexOf('?');
        if (question >= 0)
        {
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(local[question..].Split('#')[0]);
            if (query.TryGetValue("context", out var value) && ResourceScopeRef.TryParse(value.ToString(), out var scope))
                url = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(url, "context", scope.Value);
        }
        return url;
    }
    public static bool IsRepairPath(string path) => new[] { "/modelproviders", "/modelprofiles", "/extensions" }
        .Any(prefix => path.Equals(prefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
    public static bool IsInspectionPath(string path) => new[]
        { "/agents", "/flows", "/schemas", "/modelproviders", "/modelprofiles", "/runtimeprofiles", "/extensions", "/tools",
          "/data-sources", "/data-source-profiles", "/knowledge-sources", "/parameters", "/secrets", "/storages", "/storage-types",
          "/connected-services", "/workspaces", "/tenants", "/packs", "/settings", "/entries", "/resource-plans", "/transformation-functions" }
        .Any(prefix => path.Equals(prefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase));
}
