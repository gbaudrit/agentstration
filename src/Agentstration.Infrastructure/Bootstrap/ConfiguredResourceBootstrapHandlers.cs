using System.Text.Json;
using System.Text;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Schemas;
using Agentstration.Secrets;
using Agentstration.Secrets.Abstractions;
using Microsoft.Extensions.Configuration;

namespace Agentstration.Infrastructure.Bootstrap;

public sealed class ConnectedServiceBootstrapResourceHandler(ConnectedServiceManagementService services) : IBootstrapResourceHandler
{
    public string Kind => ConnectedServiceResourceKinds.ConnectedService;
    public BootstrapProfileScope Scope => BootstrapProfileScope.Instance;
    public bool SupportsProfileScope(BootstrapProfileScope profileScope) => true;
    public async Task<BootstrapResourcePlanResult> PlanAsync(BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation, BootstrapPlanningContext planning, CancellationToken cancellationToken)
    {
        var value = WorkspaceBootstrapResource.Parse<ConnectedServiceResource>(resource) with
        { ScopeRef = DataSourceProfileBootstrapResourceHandler.TargetScope(operation) };
        if (await services.GetExactAsync(value.ScopeRef.Value, value.Namespace, value.Name, cancellationToken) is not null)
            return new(BootstrapResourceDisposition.Skip);
        await services.ValidateForCreateAsync(value, cancellationToken);
        return WorkspaceBootstrapResource.Created(planning, Kind, value.Name, value.Namespace, resource);
    }
    public async Task<BootstrapResourceApplyResult> ApplyAsync(BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation, CancellationToken cancellationToken)
    {
        var value = WorkspaceBootstrapResource.Parse<ConnectedServiceResource>(resource) with
        { ScopeRef = DataSourceProfileBootstrapResourceHandler.TargetScope(operation) };
        if (await services.GetExactAsync(value.ScopeRef.Value, value.Namespace, value.Name, cancellationToken) is not null)
            return BootstrapResourceApplyResult.Skipped;
        _ = await services.CreateAsync(value, cancellationToken);
        return BootstrapResourceApplyResult.Created;
    }
}

public sealed class VaultBootstrapResourceHandler(SecretManagementService services) : IBootstrapResourceHandler
{
    public string Kind => SecretResourceKinds.Vault;
    public BootstrapProfileScope Scope => BootstrapProfileScope.Instance;
    public bool SupportsProfileScope(BootstrapProfileScope profileScope) => true;
    public async Task<BootstrapResourcePlanResult> PlanAsync(BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation, BootstrapPlanningContext planning, CancellationToken cancellationToken)
    {
        var value = WorkspaceBootstrapResource.Parse<VaultResource>(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(value.Definition.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(value.Definition.ProviderType);
        if (await services.GetVaultExactAsync(DataSourceProfileBootstrapResourceHandler.TargetScope(operation), value.Name, cancellationToken) is not null)
            return new(BootstrapResourceDisposition.Skip);
        return WorkspaceBootstrapResource.Created(planning, Kind, value.Name, value.Namespace, resource);
    }
    public async Task<BootstrapResourceApplyResult> ApplyAsync(BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation, CancellationToken cancellationToken)
    {
        var value = WorkspaceBootstrapResource.Parse<VaultResource>(resource) with
        { ScopeRef = DataSourceProfileBootstrapResourceHandler.TargetScope(operation) };
        if (await services.GetVaultExactAsync(value.ScopeRef.Value, value.Name, cancellationToken) is not null)
            return BootstrapResourceApplyResult.Skipped;
        _ = await services.CreateVaultAsync(value, cancellationToken);
        return BootstrapResourceApplyResult.Created;
    }
}

public sealed class SecretBootstrapResourceHandler(SecretManagementService services, IConfiguration configuration) : IBootstrapResourceHandler
{
    public string Kind => SecretResourceKinds.Secret;
    public BootstrapProfileScope Scope => BootstrapProfileScope.Instance;
    public bool SupportsProfileScope(BootstrapProfileScope profileScope) => true;
    public async Task<BootstrapResourcePlanResult> PlanAsync(BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation, BootstrapPlanningContext planning, CancellationToken cancellationToken)
    {
        var value = WorkspaceBootstrapResource.Parse<SecretResource>(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(value.Definition.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(value.Definition.Key);
        var scope = DataSourceProfileBootstrapResourceHandler.TargetScope(operation);
        var existing = await services.GetSecretExactAsync(scope, value.Name, cancellationToken);
        if (existing is not null && (await services.GetSecretViewExactAsync(scope, value.Name, cancellationToken)).ValueStatus == SecretValueStatus.Configured)
            return new(BootstrapResourceDisposition.Skip);
        if (!resource.Definition.TryGetProperty("valueFrom", out var initializer) || !initializer.TryGetProperty("configuration", out _))
            throw new InvalidOperationException("bootstrap_secret_initializer_required");
        if (!WorkspaceBootstrapResource.IsAvailable(planning, SecretResourceKinds.Vault, value.Definition.Vault.Name,
                value.Definition.Vault.Namespace ?? value.Namespace)
            && await services.GetVaultExactAsync(value.Definition.Vault.ScopeRef ?? scope, value.Definition.Vault.Name, cancellationToken) is null)
            throw new InvalidOperationException("bootstrap_vault_not_planned");
        return WorkspaceBootstrapResource.Created(planning, Kind, value.Name, value.Namespace, resource);
    }
    public async Task<BootstrapResourceApplyResult> ApplyAsync(BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation, CancellationToken cancellationToken)
    {
        var scope = DataSourceProfileBootstrapResourceHandler.TargetScope(operation);
        var value = WorkspaceBootstrapResource.Parse<SecretResource>(resource) with { ScopeRef = scope };
        if (await services.GetSecretExactAsync(scope, value.Name, cancellationToken) is not null)
        {
            var status = (await services.GetSecretViewExactAsync(scope, value.Name, cancellationToken)).ValueStatus;
            if (status == SecretValueStatus.Configured) return BootstrapResourceApplyResult.Skipped;
            if (status != SecretValueStatus.Missing)
            {
                if (resource.Definition.TryGetProperty("valueFrom", out var source) && source.TryGetProperty("configuration", out var sourceKey)
                    && operation.Inputs.Any(input => input.Profile == operation.ProfileName && input.Configuration == sourceKey.GetString() && input.Kind == "secret"))
                    return BootstrapResourceApplyResult.Skipped;
                throw new InvalidOperationException("bootstrap_secret_unavailable");
            }
        }
        if (!resource.Definition.TryGetProperty("valueFrom", out var initializer)
            || !initializer.TryGetProperty("configuration", out var key))
            throw new InvalidOperationException("bootstrap_secret_initializer_required");
        // A Console selection is already a configured Secret. Its reference is substituted in consumers;
        // no plaintext copy or second Secret is needed.
        if (operation.Inputs.Any(input => input.Profile == operation.ProfileName && input.Configuration == key.GetString() && input.Kind == "secret"))
            return BootstrapResourceApplyResult.Skipped;
        var text = configuration[key.GetString()!];
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("bootstrap_configuration_required");
        var vaultScope = value.Definition.Vault.ScopeRef ?? scope;
        try { _ = await services.InitializeVaultExactAsync(vaultScope, value.Definition.Vault.Name, cancellationToken); }
        catch (VaultAlreadyInitializedException) { /* An initialized Vault must retain its existing key. */ }
        if (await services.GetSecretExactAsync(scope, value.Name, cancellationToken) is null)
            _ = await services.CreateSecretAsync(value, cancellationToken);
        using var secret = new SecretValue(Encoding.UTF8.GetBytes(text));
        await services.SetValueExactAsync(scope, value.Name, secret, cancellationToken);
        return BootstrapResourceApplyResult.Created;
    }
}

public sealed class SchemaBootstrapResourceHandler(SchemaManagementService services) : IBootstrapResourceHandler
{
    public string Kind => SchemaResourceKinds.Schema;
    public BootstrapProfileScope Scope => BootstrapProfileScope.Instance;
    public bool SupportsProfileScope(BootstrapProfileScope profileScope) => true;
    public async Task<BootstrapResourcePlanResult> PlanAsync(BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation, BootstrapPlanningContext planning, CancellationToken cancellationToken)
    {
        var value = WorkspaceBootstrapResource.Parse<SchemaResource>(resource) with
        { ScopeRef = DataSourceProfileBootstrapResourceHandler.TargetScope(operation) };
        await services.ValidateForCreateAsync(value, cancellationToken);
        if (resource.Definition.TryGetProperty("publishVersion", out var publication))
        {
            SchemaManagementService.ValidatePublicationVersion(publication.GetString()!);
            var published = await services.GetVersionAsync(value.ScopeRef.Value, value.Namespace, value.Name, publication.GetString()!, cancellationToken);
            if (published is not null && (!JsonElement.DeepEquals(published.Value.Content, value.Definition.Content)
                || published.Value.Format != value.Definition.Format || published.Value.Dialect != value.Definition.Dialect))
                throw new SchemaValidationException("schema_version_immutable", "The published Schema differs from this profile. Review explicitly.");
        }
        if (await services.GetExactAsync(value.ScopeRef.Value, value.Namespace, value.Name, cancellationToken) is not null
            && (!resource.Definition.TryGetProperty("publishVersion", out var version)
                || await services.GetVersionAsync(value.ScopeRef.Value, value.Namespace, value.Name, version.GetString()!, cancellationToken) is not null))
            return new(BootstrapResourceDisposition.Skip);
        return WorkspaceBootstrapResource.Created(planning, Kind, value.Name, value.Namespace, resource);
    }
    public async Task<BootstrapResourceApplyResult> ApplyAsync(BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation, CancellationToken cancellationToken)
    {
        var value = WorkspaceBootstrapResource.Parse<SchemaResource>(resource) with
        { ScopeRef = DataSourceProfileBootstrapResourceHandler.TargetScope(operation) };
        var existing = await services.GetExactAsync(value.ScopeRef.Value, value.Namespace, value.Name, cancellationToken);
        if (existing is null) _ = await services.CreateAsync(value, cancellationToken);
        if (resource.Definition.TryGetProperty("publishVersion", out var version))
        {
            if (await services.GetVersionAsync(value.ScopeRef.Value, value.Namespace, value.Name, version.GetString()!, cancellationToken) is null)
            {
                // Never publish an operator-edited draft as the profile's intended content.
                if (existing is not null && !JsonElement.DeepEquals(existing.Value.Definition.Content, value.Definition.Content))
                    return BootstrapResourceApplyResult.Conflict;
                _ = await services.PublishAsync(value.ScopeRef.Value, value.Namespace, value.Name, version.GetString()!, cancellationToken);
                return BootstrapResourceApplyResult.Created;
            }
        }
        return existing is null ? BootstrapResourceApplyResult.Created : BootstrapResourceApplyResult.Skipped;
    }
}
