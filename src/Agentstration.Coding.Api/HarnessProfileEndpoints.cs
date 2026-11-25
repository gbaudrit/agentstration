using Agentstration.Coding.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Web.Security;
using Agentstration.Work;

namespace Agentstration.Coding.Api;

public static partial class CodingApiModule
{
    private static void MapHarnessProfiles(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/workspaces/{workspaceName}/coding").AddEndpointFilter(RequireWorkspaceAsync).WithTags("Coding");
        group.MapPost("/harness-configurations", async (CreateHarnessNativeConfigurationRequest request, HarnessProfileService service,
            ICurrentRequestContext current, HttpContext http, CancellationToken token) =>
                HarnessResource(await service.CreateConfigurationAsync(Workspace(current), request, token), http, true))
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(64 * 1024));
        group.MapGet("/namespaces/{resourceNamespace}/harness-configurations/{name}/versions/{version}", async (string resourceNamespace, string name, string version,
            HarnessProfileService service, ICurrentRequestContext current, HttpContext http, CancellationToken token) =>
                HarnessResource(await service.GetConfigurationAsync(Workspace(current), ResourceNamespace.Parse(resourceNamespace), name, version, token), http))
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPost("/harness-profiles", async (HarnessProfileResource request, HarnessProfileService service, ICurrentRequestContext current, HttpContext http, CancellationToken token) =>
                HarnessResource(await service.CreateAsync(Workspace(current), request, token), http, true))
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(64 * 1024));
        group.MapGet("/namespaces/{resourceNamespace}/harness-profiles/{name}", async (string resourceNamespace, string name, HarnessProfileService service,
            ICurrentRequestContext current, HttpContext http, CancellationToken token) =>
                HarnessResource(await service.GetAsync(Workspace(current), ResourceNamespace.Parse(resourceNamespace), name, token), http))
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPut("/namespaces/{resourceNamespace}/harness-profiles/{name}", async (string resourceNamespace, string name, HarnessProfileUpdateRequest request,
            HarnessProfileService service, ICurrentRequestContext current, HttpContext http, CancellationToken token) =>
                HarnessResource(await service.UpdateAsync(Workspace(current), ResourceNamespace.Parse(resourceNamespace), name, request.Definition, request.ExpectedETag, token), http))
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(64 * 1024));
        group.MapPost("/namespaces/{resourceNamespace}/harness-profiles/{name}/versions", async (string resourceNamespace, string name, PublishHarnessProfileRequest request,
            HarnessProfileService service, ICurrentRequestContext current, HttpContext http, CancellationToken token) =>
                HarnessResource(await service.PublishAsync(Workspace(current), ResourceNamespace.Parse(resourceNamespace), name, request, token), http, true))
            .RequireAuthorization(AgentstrationPolicies.CanWriteResources);
        group.MapGet("/namespaces/{resourceNamespace}/harness-profiles/{name}/versions/{version}", async (string resourceNamespace, string name, string version,
            HarnessProfileService service, ICurrentRequestContext current, HttpContext http, CancellationToken token) =>
                HarnessResource(await service.GetVersionAsync(Workspace(current), ResourceNamespace.Parse(resourceNamespace), name, version, token), http))
            .RequireAuthorization(AgentstrationPolicies.CanReadResources);
        group.MapPost("/harness-preview", async (HarnessExportRequest request, HarnessProfileService service, ICurrentRequestContext current, CancellationToken token) =>
            {
                var result = await service.PreviewAsync(Workspace(current), request, token);
                return result.IsSuccess ? Results.Ok(result.Value) : HarnessProblem(result.ErrorCode!);
            })
            .RequireAuthorization(AgentstrationPolicies.CanReadResources).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(64 * 1024));
        group.MapPost("/harness-export", async (HarnessExportRequest request, HarnessProfileService service, ICurrentRequestContext current, HttpContext http, CancellationToken token) =>
            {
                var result = await service.ExportAsync(Workspace(current), request, token);
                if (!result.IsSuccess) return HarnessProblem(result.ErrorCode!);
                http.Response.Headers.ETag = '"' + result.Value!.Digest + '"';
                http.Response.Headers["X-Agentstration-Harness-Digest"] = result.Value.FilesDigest;
                return Results.File(result.Value.Content, "application/zip", result.Value.FileName);
            }).RequireAuthorization(AgentstrationPolicies.CanReadResources).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(64 * 1024));
    }
    private static IResult HarnessResource<T>(Result<StoredResource<T>> result, HttpContext http, bool created = false) where T : Resource
    {
        if (!result.IsSuccess) return HarnessProblem(result.ErrorCode!);
        http.Response.Headers.ETag = result.Value!.ETag;
        return Results.Json(result.Value.Value, statusCode: created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
    }
    private static IResult HarnessProblem(string code) => Results.Problem(title: code, statusCode: code.EndsWith("not_found", StringComparison.Ordinal) ? 404
        : code.EndsWith("denied", StringComparison.Ordinal) ? 403 : code.Contains("conflict", StringComparison.Ordinal) || code.StartsWith("resource_", StringComparison.Ordinal) ? 409 : 400);
}
