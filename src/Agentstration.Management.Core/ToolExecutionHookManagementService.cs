using System.Text.Json;
using Agentstration.Management.Abstractions;
using Agentstration.Resources;

namespace Agentstration.Management.Core;

public sealed class ToolExecutionHookValidationException(string message) : Exception(message);

public sealed class ToolExecutionHookManagementService(IControlPlaneStore store)
{
    private static readonly HashSet<string> DenyConfigurationKeys = new(StringComparer.Ordinal)
    {
        "code",
        "message"
    };

    public async Task<StoredResource<ToolExecutionHookResource>> CreateAsync(
        ToolExecutionHookResource resource,
        CancellationToken cancellationToken)
    {
        Validate(resource);
        if (await GetAsync(resource.Namespace, resource.Name, cancellationToken) is not null)
            throw new ControlPlaneConcurrencyException($"Tool execution hook '{resource.Address}' already exists.");
        return await store.PutAsync(resource with
        {
            Generation = 1,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        }, null, true, cancellationToken);
    }

    public Task<StoredResource<ToolExecutionHookResource>?> GetAsync(
        ResourceNamespace @namespace,
        string name,
        CancellationToken cancellationToken) =>
        store.GetAsync<ToolExecutionHookResource>(
            new ResourceKey(ResourceKinds.ToolExecutionHook, name, @namespace),
            cancellationToken);

    public Task<IReadOnlyList<StoredResource<ToolExecutionHookResource>>> ListAsync(CancellationToken cancellationToken) =>
        store.ListAllAsync<ToolExecutionHookResource>(ResourceKinds.ToolExecutionHook, cancellationToken);

    public async Task<StoredResource<ToolExecutionHookResource>> PutAsync(
        ResourceNamespace @namespace,
        string name,
        ToolExecutionHookProperties definition,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        var existing = await GetAsync(@namespace, name, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.ToolExecutionHook, name, @namespace));
        var updated = existing.Value with
        {
            Generation = checked(existing.Value.Generation + 1),
            Definition = definition,
            Status = new ResourceStatus { ProvisioningState = ProvisioningState.Succeeded }
        };
        Validate(updated);
        return await store.PutAsync(updated, ifMatch, false, cancellationToken);
    }

    public async Task DeleteAsync(
        ResourceNamespace @namespace,
        string name,
        string? ifMatch,
        CancellationToken cancellationToken)
    {
        _ = await GetAsync(@namespace, name, cancellationToken)
            ?? throw new ControlPlaneResourceNotFoundException(new(ResourceKinds.ToolExecutionHook, name, @namespace));
        await store.DeleteAsync(new ResourceKey(ResourceKinds.ToolExecutionHook, name, @namespace), ifMatch, cancellationToken);
    }

    public static void Validate(ToolExecutionHookResource resource)
    {
        if (resource.Kind != ResourceKinds.ToolExecutionHook)
            throw new ToolExecutionHookValidationException($"Kind must be '{ResourceKinds.ToolExecutionHook}'.");
        if (resource.ApiVersion != ManagementApiVersions.CoreV1)
            throw new ToolExecutionHookValidationException($"ApiVersion must be '{ManagementApiVersions.CoreV1}'.");
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.Metadata.Name);
        if (string.IsNullOrWhiteSpace(resource.Definition.DisplayName) || resource.Definition.DisplayName.Length > 256)
            throw new ToolExecutionHookValidationException("Tool execution hook displayName must contain 1 to 256 characters.");
        if (resource.Definition.Order is < -10_000 or > 10_000)
            throw new ToolExecutionHookValidationException("Tool execution hook order must be between -10000 and 10000.");
        if (!string.Equals(resource.Definition.Handler, ToolExecutionHookHandlers.Deny, StringComparison.Ordinal))
            throw new ToolExecutionHookValidationException($"Unsupported Tool execution hook handler '{resource.Definition.Handler}'.");

        ValidateSelector(resource.Definition.Selector.Tools, "tools");
        ValidateSelector(resource.Definition.Selector.Providers, "providers");
        ValidateSelector(resource.Definition.Selector.Agents, "agents");
        ValidateDenyConfiguration(resource.Definition.Configuration);
    }

    private static void ValidateSelector(IReadOnlyList<string> values, string name)
    {
        if (values.Count > 256)
            throw new ToolExecutionHookValidationException($"Tool execution hook selector '{name}' cannot contain more than 256 values.");
        if (values.Any(string.IsNullOrWhiteSpace) || values.Any(value => value.Length > 256))
            throw new ToolExecutionHookValidationException($"Tool execution hook selector '{name}' contains an invalid resource identity.");
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            throw new ToolExecutionHookValidationException($"Tool execution hook selector '{name}' contains duplicate resource identities.");
    }

    private static void ValidateDenyConfiguration(IReadOnlyDictionary<string, JsonElement> configuration)
    {
        var unknown = configuration.Keys.FirstOrDefault(key => !DenyConfigurationKeys.Contains(key));
        if (unknown is not null)
            throw new ToolExecutionHookValidationException($"Deny hook configuration property '{unknown}' is not supported.");
        var code = RequiredString(configuration, "code");
        var message = RequiredString(configuration, "message");
        if (code.Length > 128 || !char.IsLetterOrDigit(code[0]) || code.Any(value => !char.IsLetterOrDigit(value) && value is not '_' and not '-' and not '.'))
            throw new ToolExecutionHookValidationException("Deny hook code must contain 1 to 128 letters, digits, '.', '_' or '-' and start with a letter or digit.");
        if (message.Length > 2048)
            throw new ToolExecutionHookValidationException("Deny hook message cannot exceed 2048 characters.");
    }

    public static string RequiredString(IReadOnlyDictionary<string, JsonElement> configuration, string name)
    {
        if (!configuration.TryGetValue(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ToolExecutionHookValidationException($"Deny hook configuration requires a non-empty string '{name}'.");
        return value.GetString()!;
    }
}
