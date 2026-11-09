using System.Globalization;
using System.Security.Cryptography;
using Agentstration.Models;
using Agentstration.Models.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Web.Security;
using Microsoft.AspNetCore.DataProtection;

namespace Agentstration.Web.Api.Models;

internal static class ModelCollectionCursor
{
    internal static string? Read(string? cursor, IDataProtector protector)
    {
        if (cursor is null) return null;
        if (cursor.Length > 4096) throw new ArgumentException("Invalid continuation.");
        try { return protector.Unprotect(cursor); }
        catch (CryptographicException) { throw new ArgumentException("Invalid continuation."); }
    }
}

internal sealed class LookupModelProfilesEndpoint : IModelManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/queries/lookup", HandleAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
    private static Task<IResult> HandleAsync(string? search, string? provider, string? ownerKind, string? cursor, ModelProfileManagementService service, IDataProtectionProvider protection, CancellationToken cancellationToken) =>
        ModelsApiHttp.ExecuteAsync(async () =>
        {
            if (search?.Length > 256 || provider?.Length > 256) throw new ArgumentException("The lookup query is too long.");
            if (!string.IsNullOrEmpty(ownerKind) && !Enum.TryParse<ResourceScopeKind>(ownerKind, out _)) throw new ArgumentException("Invalid owner kind.");
            var protector = protection.CreateProtector("ModelProfileLookup.v1", search ?? "", provider ?? "", ownerKind ?? "");
            var rawOffset = ModelCollectionCursor.Read(cursor, protector);
            var offset = 0;
            if (rawOffset is not null && (!int.TryParse(rawOffset, out offset) || offset < 0 || offset > int.MaxValue - 100)) throw new ArgumentException("Invalid continuation.");
            var page = await service.LookupPageAsync(search, provider, ownerKind, offset, cancellationToken);
            var values = new List<ModelProfileSummaryResponse>();
            foreach (var profile in page.Value)
            {
                var resolved = await service.ResolveAsync(profile, cancellationToken);
                values.Add(new(profile.Name, profile.Name, new(profile.Definition.DisplayName, profile.Definition.Description,
                    new(profile.Definition.Provider.Name, profile.Definition.Provider.Name, resolved.Provider?.DisplayName),
                    new(profile.Definition.Model.Name), profile.Definition.Generation, profile.Definition.Reasoning, profile.Definition.Output, resolved.Status, null), profile.Namespace.Value, profile.ScopeRef));
            }
            return Results.Ok(new ModelProfileLookupPageResponse(values, page.NextOffset is { } next ? protector.Protect(next.ToString(CultureInfo.InvariantCulture)) : null));
        });
}

internal sealed class GetModelProfileUsagePageEndpoint : IModelManagementEndpoint
{
    public static void Map(RouteGroupBuilder group) => group.MapGet("/{profileName}/usages/page", HandleAsync).RequireAuthorization(AgentstrationPolicies.CanReadResources);
    private static Task<IResult> HandleAsync(string profileName, string? resourceNamespace, string? scopeRef, string? search, string? cursor, ModelProfileManagementService service, IDataProtectionProvider protection, CancellationToken cancellationToken) =>
        ModelsApiHttp.ExecuteAsync(async () =>
        {
            if (search?.Length > 256) throw new ArgumentException("The usage query is too long.");
            var profile = await service.GetAsync(ModelsApiHttp.Namespace(resourceNamespace), profileName, ModelProfileScope.Parse(scopeRef), cancellationToken)
                ?? throw new ResourceNotFoundException(new(ModelResourceKinds.ModelProfile, profileName, ModelsApiHttp.Namespace(resourceNamespace)));
            var protector = protection.CreateProtector("ModelProfileUsages.v1", profile.Value.Uid.ToString("D"), search ?? "");
            var page = await service.GetUsagePageAsync(profile.Value, search, ModelCollectionCursor.Read(cursor, protector), cancellationToken);
            var values = page.Value.Select(value => new ModelProfileUsageResponse(value.Kind, value.Name, value.Name, value.DisplayName, value.Namespace.Value, value.ScopeRef, value.ScopeDisplayName)).ToArray();
            return Results.Ok(new ModelProfileUsagePageResponse(values, page.NextCursor is { } next ? protector.Protect(next) : null, cursor is null && page.NextCursor is null ? values.Length : null));
        });
}
