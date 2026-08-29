using System.Text.Json;
using Agentstration.Management.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;

namespace Agentstration.Security.AspNetCoreIdentity;

public sealed record ConfigurationValueReference
{
    public string Configuration { get; init; } = string.Empty;
}

public sealed record PlatformAdministratorBootstrapDefinition
{
    public string DisplayName { get; init; } = string.Empty;
    public string? Email { get; init; }
    public ConfigurationValueReference PasswordFrom { get; init; } = new();
}

public sealed class PlatformAdministratorBootstrapHandler(
    UserManager<LocalIdentityUser> users,
    LocalBootstrapCoordinator bootstrap,
    IPrincipalResolver principalResolver,
    IPlatformAuthorizationService authorization,
    IConfiguration configuration) : IBootstrapResourceHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Kind => BootstrapResourceKinds.PlatformAdministrator;

    public async Task<BootstrapResourceApplyResult> ApplyAsync(
        BootstrapResourceDocument resource,
        CancellationToken cancellationToken)
    {
        var userName = resource.Metadata.Name.Trim();
        if (userName.Length is < 3 or > 64)
            throw new InvalidOperationException("PlatformAdministrator metadata.name must contain between 3 and 64 characters.");

        var definition = resource.Definition.Deserialize<PlatformAdministratorBootstrapDefinition>(JsonOptions)
            ?? throw new InvalidOperationException("PlatformAdministrator definition is required.");
        if (string.IsNullOrWhiteSpace(definition.DisplayName) || definition.DisplayName.Trim().Length is < 2 or > 120)
            throw new InvalidOperationException("PlatformAdministrator definition.displayName must contain between 2 and 120 characters.");
        if (string.IsNullOrWhiteSpace(definition.PasswordFrom.Configuration))
            throw new InvalidOperationException("PlatformAdministrator definition.passwordFrom.configuration is required.");

        var existing = await users.FindByNameAsync(userName);
        if (existing is not null)
        {
            var principal = await principalResolver.ResolveLocalAsync(existing.Id, cancellationToken);
            if (principal is not null && await authorization.IsPlatformAdministratorAsync(principal.Id, cancellationToken))
                return BootstrapResourceApplyResult.Skipped;
            throw new InvalidOperationException(
                $"Local account '{userName}' already exists but is not the declared Platform administrator.");
        }

        var password = configuration[definition.PasswordFrom.Configuration];
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException(
                $"Configuration value '{definition.PasswordFrom.Configuration}' referenced by PlatformAdministrator '{userName}' is missing.");

        var result = await bootstrap.BootstrapAsync(new(
            userName,
            password,
            definition.DisplayName.Trim(),
            string.IsNullOrWhiteSpace(definition.Email) ? null : definition.Email.Trim()), cancellationToken);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"PlatformAdministrator '{userName}' could not be created: {string.Join("; ", result.Errors)}");
        return BootstrapResourceApplyResult.Created;
    }
}
