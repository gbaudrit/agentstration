using System.Globalization;
using System.Text;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi;

namespace Agentstration.Web.Configuration;

public static class OpenApiConfiguration
{
    public const string DocumentName = "v1";
    public const string DocumentPath = "/openapi/v1.json";
    public const string SwaggerPath = "/swagger";

    private const string BearerScheme = "Bearer";
    private const string CookieScheme = "ApplicationCookie";

    public static IServiceCollection AddAgentstrationOpenApi(this IServiceCollection services)
    {
        services.AddOpenApi(DocumentName, options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Agentstration HTTP API",
                    Version = DocumentName,
                    Description = "Authoritative HTTP API for Agentstration's Management, Runtime, Work, Flow, identity and Workplace surfaces. SignalR and MCP are separate transports and are not represented by this document."
                };
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal);
                document.Components.SecuritySchemes[BearerScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT or Agentstration PAT",
                    Description = "JWT bearer token used in OIDC and Hybrid authentication modes, or an Agentstration personal access token."
                };
                document.Components.SecuritySchemes[CookieScheme] = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    In = ParameterLocation.Cookie,
                    Name = AgentstrationAuthenticationDefaults.ApplicationCookie,
                    Description = "Agentstration local application session cookie. A browser signed in to the Console sends it automatically."
                };
                RemoveUnreferencedTags(document);
                return Task.CompletedTask;
            });
            options.AddOperationTransformer(async (operation, context, cancellationToken) =>
            {
                var description = context.Description;
                var document = context.Document ?? throw new InvalidOperationException("The OpenAPI operation is not attached to a document.");
                var metadata = description.ActionDescriptor.EndpointMetadata;
                var path = NormalizePath("/" + (description.RelativePath ?? string.Empty).TrimStart('/'));
                var method = description.HttpMethod
                    ?? metadata.OfType<HttpMethodMetadata>().SelectMany(value => value.HttpMethods).FirstOrDefault()
                    ?? "HTTP";
                operation.OperationId ??= OperationId(method, path);

                var tagName = TagFor(path);
                document.Tags ??= new HashSet<OpenApiTag>();
                if (!document.Tags.Any(tag => string.Equals(tag.Name, tagName, StringComparison.Ordinal)))
                    document.Tags.Add(new OpenApiTag { Name = tagName });
                operation.Tags = new HashSet<OpenApiTagReference>
                {
                    new(tagName, document, externalResource: null)
                };

                var problemSchema = await context.GetOrCreateSchemaAsync(typeof(ProblemDetails), parameterDescription: null, cancellationToken);
                operation.Responses ??= new OpenApiResponses();
                if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                    operation.Responses.TryAdd("default", ProblemResponse("An expected validation, concurrency, authorization or resource error.", problemSchema));
                await DocumentSpecialFormatsAsync(operation, context, method, path, cancellationToken);
                await DocumentSuccessResponseAsync(operation, context, method, path, cancellationToken);

                var allowsAnonymous = metadata.OfType<IAllowAnonymous>().Any();
                var requiresAuthorization = !allowsAnonymous && metadata.OfType<IAuthorizeData>().Any();
                if (!requiresAuthorization) return;

                operation.Security =
                [
                    new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(BearerScheme, document, externalResource: null)] = []
                    },
                    new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference(CookieScheme, document, externalResource: null)] = []
                    }
                ];

                operation.Responses.TryAdd("401", ProblemResponse("Authentication is required.", problemSchema));
                operation.Responses.TryAdd("403", ProblemResponse("The authenticated principal is not authorized for this operation.", problemSchema));
            });
        });
        return services;
    }

    public static IApplicationBuilder MapAgentstrationOpenApi(this WebApplication app)
    {
        app.MapOpenApi();
        app.UseSwaggerUI(options =>
        {
            options.RoutePrefix = SwaggerPath.TrimStart('/');
            options.SwaggerEndpoint(DocumentPath, "Agentstration HTTP API v1");
            options.DocumentTitle = "Agentstration HTTP API";
            options.DisplayRequestDuration();
            options.EnableTryItOutByDefault();
            options.EnablePersistAuthorization();
        });
        return app;
    }

    private static OpenApiResponse ProblemResponse(string description, IOpenApiSchema schema) => new()
    {
        Description = description,
        Content = new Dictionary<string, OpenApiMediaType>(StringComparer.OrdinalIgnoreCase)
        {
            ["application/problem+json"] = new() { Schema = schema }
        }
    };

    private static async Task DocumentSpecialFormatsAsync(
        OpenApiOperation operation,
        Microsoft.AspNetCore.OpenApi.OpenApiOperationTransformerContext context,
        string method,
        string path,
        CancellationToken cancellationToken)
    {
        if (HttpMethods.IsPost(method) && IsPackArchiveUpload(path))
        {
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Description = "Pack ZIP archive. Installation also accepts a multipart form with an archive and optional JSON bindings.",
                Content = new Dictionary<string, OpenApiMediaType>(StringComparer.OrdinalIgnoreCase)
                {
                    ["application/zip"] = new() { Schema = BinarySchema() },
                    ["application/octet-stream"] = new() { Schema = BinarySchema() },
                    ["multipart/form-data"] = new() { Schema = PackInstallationFormSchema() }
                }
            };
            return;
        }

        if (HttpMethods.IsGet(method) && path.EndsWith("/download", StringComparison.OrdinalIgnoreCase))
        {
            operation.Responses!["200"] = Response("Pack build ZIP archive.", "application/zip", BinarySchema());
            return;
        }

        if (HttpMethods.IsGet(method) && path.EndsWith("/events", StringComparison.OrdinalIgnoreCase)
            && (path.StartsWith("/api/flowRuns/", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("/api/runtime/runs/", StringComparison.OrdinalIgnoreCase)))
            operation.Responses!["200"] = Response("Server-sent event stream. Reconnect with Last-Event-ID where supported.", "text/event-stream", StringSchema());
    }

    private static async Task DocumentSuccessResponseAsync(
        OpenApiOperation operation,
        Microsoft.AspNetCore.OpenApi.OpenApiOperationTransformerContext context,
        string method,
        string path,
        CancellationToken cancellationToken)
    {
        var contract = OpenApiSuccessResponseCatalog.Resolve(method, path);
        if (contract is null) return;

        operation.Summary = contract.Summary;
        operation.Description ??= contract.Description;
        operation.Responses ??= new OpenApiResponses();

        if (HasContent(operation, contract.StatusCode)) return;
        if (contract.StatusCode != StatusCodes.Status200OK
            && operation.Responses.TryGetValue("200", out var inferred)
            && inferred is OpenApiResponse { Content.Count: 0 })
            operation.Responses.Remove("200");

        var statusCode = contract.StatusCode.ToString(CultureInfo.InvariantCulture);
        if (contract.BodyType is null)
        {
            operation.Responses[statusCode] = new OpenApiResponse { Description = contract.ResponseDescription };
            return;
        }

        var schema = await context.GetOrCreateSchemaAsync(contract.BodyType, parameterDescription: null, cancellationToken);
        operation.Responses[statusCode] = Response(contract.ResponseDescription, contract.MediaType, schema);
    }

    private static bool HasContent(OpenApiOperation operation, int statusCode) =>
        operation.Responses?.TryGetValue(statusCode.ToString(CultureInfo.InvariantCulture), out var response) == true
        && response is OpenApiResponse { Content.Count: > 0 };

    private static bool IsPackArchiveUpload(string path) =>
        path.Equals("/api/packs", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/packs/preview", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith("/source", StringComparison.OrdinalIgnoreCase) && path.StartsWith("/api/packs/", StringComparison.OrdinalIgnoreCase);

    private static OpenApiResponse Response(string description, string mediaType, IOpenApiSchema schema) => new()
    {
        Description = description,
        Content = new Dictionary<string, OpenApiMediaType>(StringComparer.OrdinalIgnoreCase)
        {
            [mediaType] = new() { Schema = schema }
        }
    };

    private static OpenApiSchema StringSchema() => new() { Type = JsonSchemaType.String };

    private static OpenApiSchema BinarySchema() => new() { Type = JsonSchemaType.String, Format = "binary" };

    private static void RemoveUnreferencedTags(OpenApiDocument document)
    {
        if (document.Tags is null || document.Paths is null) return;

        var referencedTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in document.Paths.Values)
        {
            if (path.Operations is null) continue;
            foreach (var operation in path.Operations.Values)
            {
                if (operation.Tags is null) continue;
                foreach (var tag in operation.Tags)
                    if (tag.Name is { } name) referencedTags.Add(name);
            }
        }
        document.Tags = new HashSet<OpenApiTag>(
            document.Tags.Where(tag => tag.Name is { } name && referencedTags.Contains(name)));
    }

    private static OpenApiSchema PackInstallationFormSchema() => new()
    {
        Type = JsonSchemaType.Object,
        Required = new HashSet<string>(StringComparer.Ordinal) { "archive" },
        Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
        {
            ["archive"] = BinarySchema(),
            ["bindings"] = new OpenApiSchema { Type = JsonSchemaType.String, Description = "JSON array of Pack binding selections." }
        }
    };

    private static string OperationId(string method, string path)
    {
        var value = new StringBuilder(method.ToLowerInvariant());
        foreach (var character in path)
        {
            if (char.IsLetterOrDigit(character)) value.Append(char.ToLower(character, CultureInfo.InvariantCulture));
            else if (value.Length > 0 && value[^1] != '_') value.Append('_');
        }
        return value.ToString().TrimEnd('_');
    }

    private static string NormalizePath(string path)
    {
        var queryIndex = path.IndexOf('?', StringComparison.Ordinal);
        var routePath = queryIndex >= 0 ? path[..queryIndex] : path;
        var value = new StringBuilder(routePath.Length);
        var insideParameter = false;
        var insideConstraint = false;
        foreach (var character in routePath)
        {
            if (character == '{') insideParameter = true;
            if (insideParameter && character == ':') insideConstraint = true;
            if (!insideConstraint) value.Append(character);
            if (character != '}') continue;
            if (insideConstraint) value.Append(character);
            insideParameter = false;
            insideConstraint = false;
        }
        return value.Length > 1 ? value.ToString().TrimEnd('/') : value.ToString();
    }

    private static string TagFor(string path)
    {
        var value = path.ToLowerInvariant();
        if (value == "/health") return "System";
        if (value.StartsWith("/api/auth", StringComparison.Ordinal)) return "Authentication";
        if (value.StartsWith("/api/identity", StringComparison.Ordinal)) return "Identity";
        if (value.StartsWith("/api/workplace", StringComparison.Ordinal)) return "Workplace";
        if (value.StartsWith("/api/work/workitems", StringComparison.Ordinal)) return "Work";
        if (value.StartsWith("/api/tasks", StringComparison.Ordinal)) return "Work operations";
        if (value.StartsWith("/api/runtime", StringComparison.Ordinal)) return "Runtime";
        if (value.StartsWith("/api/flows", StringComparison.Ordinal)
            || value.StartsWith("/api/flowruns", StringComparison.Ordinal)
            || value.Contains("/flows", StringComparison.Ordinal)) return "Flows";
        if (value.StartsWith("/api/tool-governance", StringComparison.Ordinal)) return "Tool governance";
        if (value.StartsWith("/api/model", StringComparison.Ordinal)
            || value.StartsWith("/api/runtimeprofiles", StringComparison.Ordinal)) return "Model management";
        if (value.StartsWith("/api/extensions", StringComparison.Ordinal)) return "Extensions";
        if (value.StartsWith("/api/vaults", StringComparison.Ordinal)
            || value.StartsWith("/api/secrets", StringComparison.Ordinal)) return "Secrets";
        if (value.StartsWith("/api/toolproviders", StringComparison.Ordinal)
            || value.StartsWith("/api/tools", StringComparison.Ordinal)
            || value.StartsWith("/api/toolexecutionhooks", StringComparison.Ordinal)) return "Tools";
        if (value.StartsWith("/api/packs", StringComparison.Ordinal)
            || value.StartsWith("/api/pack-projects", StringComparison.Ordinal)) return "Packs";
        if (value.Contains("/triggers", StringComparison.Ordinal)) return "Triggers";
        if (value.Contains("/agents", StringComparison.Ordinal)
            || value.Contains("/deployments", StringComparison.Ordinal)
            || value.StartsWith("/api/routing", StringComparison.Ordinal)) return "Management";
        if (value.StartsWith("/api/diagnostics", StringComparison.Ordinal)) return "Diagnostics";
        if (value.StartsWith("/api/workspaces", StringComparison.Ordinal)) return "Workplace";
        return "API";
    }
}
