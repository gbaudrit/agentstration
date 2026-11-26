using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Models;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Secrets;
using Agentstration.Security.Contracts;
using Agentstration.Secrets.Abstractions;
using Microsoft.Extensions.Configuration;

namespace Agentstration.Extensions.Aep;

public sealed class ExtensionConfigurationService(
    IResourceStore store, IResourceScopeOperations scopeOperations, ICurrentRequestContext current,
    IRequestContextScopeFactory requestScopes, IHttpClientFactory clients, ISecretResolver secrets,
    IParameterResolver parameters, ISecretCapabilityService capabilities, IConfiguration configuration,
    AepTransportSecurityOptions transportOptions, ISecurityAuditWriter audit) : IExtensionConfigurationService, IExtensionConfigurationSynchronizer, IDisposable
{
    // Bounded stripes serialize saves, refreshes and observed-state updates for an installation.
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public Task<ExtensionConfigurationView> GetAsync(ResourceNamespace resourceNamespace, string registrationName, CancellationToken cancellationToken) =>
        AuthorizedAsync(AuthorizationPermissions.ResourcesRead, async token =>
        {
            var registration = await RegistrationAsync(resourceNamespace, registrationName, token, requireAuthenticated: false);
            var saved = await StoredAsync(registration, token);
            if (registration.Definition.AuthenticationMode != AepTransportAuthenticationMode.StaticBearer
                || string.IsNullOrWhiteSpace(registration.Definition.ExpectedExtensionId))
                return new(false, null, saved?.Value, saved?.ETag, false, new Dictionary<string, JsonElement>());
            try
            {
                using var http = CreateHttp(registration);
                var state = await Client(http, registration).GetOperationalConfigurationAsync(token);
                return View(saved, state);
            }
            catch (AepProtocolException) when (saved is not null)
            {
                return new(true, null, saved.Value, saved.ETag, false, new Dictionary<string, JsonElement>());
            }
        }, cancellationToken);

    public Task<ExtensionConfigurationView> PutAsync(ResourceNamespace resourceNamespace, string registrationName,
        PutExtensionConfigurationRequest request, string? ifMatch, CancellationToken cancellationToken) =>
        AuthorizedAsync(AuthorizationPermissions.ResourcesWrite, async token =>
        {
            if (request.Bindings is null || request.Bindings.Any(b => b is null) || JsonSerializer.SerializeToUtf8Bytes(request).Length > AepOperationalConfigurationProtocol.MaximumBytes)
                throw Error("configuration_payload_invalid");
            var registration = await RegistrationAsync(resourceNamespace, registrationName, token);
            var gate = Gate(registration);
            await gate.WaitAsync(token);
            try
            {
                var previous = await StoredAsync(registration, token);
                if (previous is not null && (ifMatch is null || ifMatch != previous.ETag)
                    || previous is null && ifMatch is not null)
                    throw new ResourceConcurrencyException("The configuration version changed.");
                using var http = CreateHttp(registration);
                var client = Client(http, registration);
                var state = await client.GetOperationalConfigurationAsync(token)
                    ?? throw Error("configuration_unsupported");
                var descriptor = state.Descriptor;
                if (previous is not null && (previous.Value.Definition.OptionSet != descriptor.Id
                    || previous.Value.Definition.Version != descriptor.Version || previous.Value.Definition.SchemaDigest != descriptor.SchemaDigest))
                    throw Error("configuration_contract_changed");
                if (request.OptionSet != descriptor.Id || request.Version != descriptor.Version || request.SchemaDigest != descriptor.SchemaDigest)
                    throw Error("configuration_contract_changed");
                var candidate = (previous?.Value ?? new ExtensionConfigurationResource
                {
                    Uid = Guid.NewGuid(), ApiVersion = ResourceApiVersions.CoreV1,
                    Kind = ExtensionKinds.ExtensionConfiguration, ScopeRef = ResourceScopeRef.Instance,
                    Metadata = new ResourceMetadata { Name = request.Name ?? registration.Name, Namespace = registration.Namespace },
                    Definition = null!
                }) with
                {
                    Generation = checked((previous?.Value.Generation ?? 0) + 1),
                    Definition = new(registration.Uid, registration.Definition.ExpectedExtensionId!,
                        request.OptionSet, request.Version, request.SchemaDigest, request.Bindings),
                    Status = new() { ProvisioningState = ProvisioningState.Accepted },
                    Observation = (previous?.Value.Observation ?? new(null, null, "pending")) with { State = "pending", Diagnostic = null }
                };
                using var values = await ResolveAsync(registration, candidate, descriptor, token);
                var saved = await store.PutExactAsync(ResourceScopeRef.Instance, candidate, previous?.ETag, previous is null, token);
                await audit.WriteAsync(new(SecurityAuditActions.ExtensionConfigurationSaved), token);
                return await ApplyCoreAsync(registration, saved, client, state, values, token);
            }
            finally { gate.Release(); }
        }, cancellationToken);

    public Task<ExtensionConfigurationView> ApplyAsync(ResourceNamespace resourceNamespace, string registrationName,
        string ifMatch, CancellationToken cancellationToken) =>
        AuthorizedAsync(AuthorizationPermissions.ResourcesWrite, async token =>
        {
            var registration = await RegistrationAsync(resourceNamespace, registrationName, token);
            return await RefreshAsync(registration, ifMatch, token);
        }, cancellationToken);

    public async Task EnsureAsync(ResourceReference reference, ResourceNamespace ownerNamespace, CancellationToken cancellationToken)
    {
        if (reference.ScopeRef != ResourceScopeRef.Instance) return;
        using var owner = requestScopes.PushSystem();
        var registration = await store.GetExactAsync<ExtensionRegistrationResource>(ScopedResourceAddress.Create(
            ResourceScopeRef.Instance, reference.Namespace ?? ownerNamespace, ExtensionKinds.ExtensionRegistration, reference.Name), cancellationToken);
        if (registration is not null) await EnsureAsync(registration.Value, cancellationToken);
    }

    public async Task EnsureAsync(ExtensionRegistrationResource registration, CancellationToken cancellationToken)
    {
        if (registration.ScopeRef != ResourceScopeRef.Instance) return;
        using var owner = requestScopes.PushSystem();
        var saved = await StoredAsync(registration, cancellationToken);
        if (saved is null) return;
        if (!registration.Definition.Enabled || registration.Definition.AuthenticationMode != AepTransportAuthenticationMode.StaticBearer
            || string.IsNullOrWhiteSpace(registration.Definition.ExpectedExtensionId)) throw Error("configuration_enrollment_required");
        var view = await RefreshAsync(registration, null, cancellationToken);
        if (!view.Ready || view.Configuration?.Observation.State != "applied") throw Error("configuration_not_ready");
    }

    private async Task<ExtensionConfigurationView> RefreshAsync(ExtensionRegistrationResource registration, string? ifMatch, CancellationToken token)
    {
        var gate = Gate(registration);
        await gate.WaitAsync(token);
        try
        {
            var saved = await StoredAsync(registration, token) ?? throw Error("configuration_missing");
            if (ifMatch is not null && saved.ETag != ifMatch) throw new ResourceConcurrencyException("The configuration version changed.");
            using var http = CreateHttp(registration);
            var client = Client(http, registration);
            var state = await client.GetOperationalConfigurationAsync(token) ?? throw Error("configuration_unsupported");
            using var values = await ResolveAsync(registration, saved.Value, state.Descriptor, token);
            return await ApplyCoreAsync(registration, saved, client, state, values, token);
        }
        finally { gate.Release(); }
    }

    private async Task<ExtensionConfigurationView> ApplyCoreAsync(ExtensionRegistrationResource registration,
        StoredResource<ExtensionConfigurationResource> saved, AepClient client, AepOperationalConfigurationState state,
        ResolvedValues values, CancellationToken token)
    {
        var resource = saved.Value;
        try
        {
            // Revalidate mutable bindings even when the desired resource generation did not change.
            if (state.AppliedRevision != values.Revision)
                state = await client.ApplyOperationalConfigurationAsync(new(resource.Definition.OptionSet, resource.Definition.Version,
                    resource.Definition.SchemaDigest, resource.Uid, resource.Generation, values.Revision,
                    state.AppliedRevision, values.Values), token);
            if (state.Descriptor.SchemaDigest != resource.Definition.SchemaDigest || state.AppliedRevision != values.Revision)
                throw Error("configuration_acknowledgment_invalid");
            var observation = new ExtensionConfigurationObservation(resource.Generation, values.Revision, "applied");
            if (resource.Observation != observation)
            {
                saved = await store.PutExactAsync(ResourceScopeRef.Instance, resource with { Observation = observation, Status = new() { ProvisioningState = ProvisioningState.Succeeded } }, saved.ETag, false, token);
                await audit.WriteAsync(new(SecurityAuditActions.ExtensionConfigurationApplied), token);
            }
            return View(saved, state);
        }
        catch (Exception exception) when (exception is AepProtocolException or HttpRequestException or ExtensionConfigurationException)
        {
            saved = await store.PutExactAsync(ResourceScopeRef.Instance, resource with
            {
                Observation = resource.Observation with { State = "pending", Diagnostic = "configuration_delivery_failed" }
            }, saved.ETag, false, token);
            await audit.WriteAsync(new(SecurityAuditActions.ExtensionConfigurationFailed, SecurityAuditOutcome.Failed,
                ReasonCode: "configuration_delivery_failed"), token);
            return View(saved, state) with { Ready = false };
        }
    }

    private async Task<ResolvedValues> ResolveAsync(ExtensionRegistrationResource registration, ExtensionConfigurationResource resource,
        AepOperationalConfigurationDescriptor descriptor, CancellationToken token)
    {
        var definition = resource.Definition;
        if (!descriptor.IsValid() || definition.RegistrationUid != registration.Uid || definition.ExtensionId != registration.Definition.ExpectedExtensionId
            || definition.OptionSet != descriptor.Id || definition.Version != descriptor.Version || definition.SchemaDigest != descriptor.SchemaDigest
            || definition.Bindings is null || definition.Bindings.Any(b => b is null) || definition.Bindings.Count > descriptor.Options.Count
            || definition.Bindings.Any(b => !descriptor.Options.Any(o => o.Id == b.RequirementId))
            || definition.Bindings.Select(b => b.RequirementId).Distinct(StringComparer.Ordinal).Count() != definition.Bindings.Count)
            throw Error("configuration_invalid");
        var values = new List<AepBoundValue>();
        var handles = new List<SecretCapabilityHandle>();
        var consumer = ScopedResourceAddress.Create(ResourceScopeRef.Instance, resource.Namespace, resource.Kind, resource.Name);
        try
        {
            foreach (var option in descriptor.Options)
            {
                if (option.ApplyMode != AepConfigurationApplyMode.Immediate || option.Scope != AepConfigurationScope.Process)
                    throw Error("configuration_apply_mode_unsupported");
                var binding = definition.Bindings.SingleOrDefault(b => b.RequirementId == option.Id);
                if (binding is null)
                {
                    if (option.Required) throw Error("configuration_binding_missing");
                    continue;
                }
                if (!Enum.TryParse<AepConfigurationValueSource>(binding.Kind.ToString(), out var source) || !option.AllowedSources.Contains(source)
                    || option.Protection == AepValueProtection.Secured && source != AepConfigurationValueSource.Secret)
                    throw Error("configuration_source_denied");
                JsonElement value;
                switch (binding.Kind)
                {
                    case ModelProviderValueBindingKind.Literal when binding.Literal is { } literal && binding.Parameter is null && binding.Secret is null:
                        value = literal;
                        break;
                    case ModelProviderValueBindingKind.Parameter when binding.Parameter is { } parameter && binding.Secret is null && binding.Literal is null:
                        if (parameter.ScopeRef != ResourceScopeRef.Instance || parameter.Address.Kind != ParameterResourceKinds.Parameter) throw Error("configuration_binding_denied");
                        var resolved = await parameters.ResolveAsync(parameter, new(ResourceScopeRef.Instance, consumer.Address), token)
                            ?? throw Error("configuration_binding_unavailable");
                        value = resolved.Value;
                        break;
                    case ModelProviderValueBindingKind.Secret when binding.Secret is { } secret && binding.Parameter is null && binding.Literal is null:
                        if (secret.ScopeRef != ResourceScopeRef.Instance || secret.Address.Kind != SecretResourceKinds.Secret) throw Error("configuration_binding_denied");
                        var publicBase = configuration["Agentstration:Aep:SecretAccess:PublicBaseUrl"];
                        if (!Uri.TryCreate(publicBase, UriKind.Absolute, out var baseUri)
                            || baseUri.Scheme != "https" && !(baseUri.Scheme == "http" && baseUri.IsLoopback)) throw Error("configuration_secret_endpoint_unavailable");
                        var executionId = Guid.NewGuid().ToString("N");
                        var context = new SecretCapabilityContext(ScopedResourceAddress.Create(ResourceScopeRef.Instance, registration.Namespace,
                            ExtensionKinds.ExtensionRegistration, registration.Name), definition.ExtensionId, consumer, option.Id, executionId);
                        var handle = await capabilities.IssueAsync(context, new(option.Id, secret), descriptor.Options.Select(o => o.Id).ToArray(), token, token);
                        handles.Add(handle);
                        values.Add(AepBoundValue.Secured(option.Id, new(AepProtocol.SecretAccessVersion, new(baseUri, AepProtocol.SecretAccessPath),
                            definition.ExtensionId, option.Id, executionId, handle.RevealForTransport())));
                        continue;
                    default: throw Error("configuration_binding_invalid");
                }
                if (AepOptionSchemaValidator.Validate(value, option.Schema).Count != 0) throw Error("configuration_value_invalid");
                values.Add(AepBoundValue.Inline(option.Id, value));
            }
            // Secret grants are one-use and get a fresh opaque revision; never hash Secret material.
            var revision = handles.Count > 0 ? Guid.NewGuid().ToString("N")
                : Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    resource.Uid, resource.Generation, Values = values
                }, AepProtocol.JsonOptions)));
            return new(values, revision, () => { foreach (var handle in handles) capabilities.Revoke(handle); });
        }
        catch (Exception exception) when (exception is ParameterManagementException or ParameterAccessDeniedException)
        {
            foreach (var handle in handles) capabilities.Revoke(handle);
            throw Error("configuration_binding_unavailable");
        }
        catch { foreach (var handle in handles) capabilities.Revoke(handle); throw; }
    }

    private Task<T> AuthorizedAsync<T>(string permission, Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        if (current.AccessMode is not (ControlPlaneAccessMode.Instance or ControlPlaneAccessMode.System))
            throw new ResourceScopeAccessDeniedException(ResourceScopeRef.Instance);
        return scopeOperations.WriteAsync(ExtensionKinds.ExtensionConfiguration, ResourceScopeRef.Instance, permission, action, token);
    }

    private async Task<ExtensionRegistrationResource> RegistrationAsync(ResourceNamespace ns, string name, CancellationToken token, bool requireAuthenticated = true)
    {
        var registration = (await store.GetExactAsync<ExtensionRegistrationResource>(ScopedResourceAddress.Create(
            ResourceScopeRef.Instance, ns, ExtensionKinds.ExtensionRegistration, name), token))?.Value
            ?? throw Error("configuration_registration_missing");
        if (!registration.Definition.Enabled || requireAuthenticated && (registration.Definition.AuthenticationMode != AepTransportAuthenticationMode.StaticBearer
            || string.IsNullOrWhiteSpace(registration.Definition.ExpectedExtensionId))) throw Error("configuration_enrollment_required");
        return registration;
    }

    private async Task<StoredResource<ExtensionConfigurationResource>?> StoredAsync(ExtensionRegistrationResource registration, CancellationToken token)
    {
        var records = await store.ListExactAsync<ExtensionConfigurationResource>(ResourceScopeRef.Instance,
            ExtensionKinds.ExtensionConfiguration, 0, 1001, token);
        if (records.Count > 1000) throw Error("configuration_inventory_limit");
        var matches = records.Where(r => r.Value.Namespace == registration.Namespace && r.Value.Definition.RegistrationUid == registration.Uid).ToArray();
        if (matches.Length > 1) throw Error("configuration_ambiguous");
        return matches.SingleOrDefault();
    }
    private SemaphoreSlim Gate(ExtensionRegistrationResource registration) => gates[(registration.Uid.GetHashCode() & int.MaxValue) % gates.Length];
    private HttpClient CreateHttp(ExtensionRegistrationResource registration)
    {
        var http = clients.CreateClient("agentstration-aep-configuration");
        http.BaseAddress = registration.Definition.Endpoint;
        return http;
    }
    private AepClient Client(HttpClient http, ExtensionRegistrationResource registration) => new(http,
        new ConfigurationAccessTokenProvider(registration, secrets, requestScopes), transportOptions, registration.Definition.ExpectedExtensionId);

    private static ExtensionConfigurationView View(StoredResource<ExtensionConfigurationResource>? saved, AepOperationalConfigurationState? state)
    {
        var contract = state?.Descriptor;
        var outOfSync = saved is not null && state is not null && saved.Value.Observation.AppliedRevision != state.AppliedRevision;
        if (outOfSync) saved = saved! with { Value = saved.Value with { Observation = saved.Value.Observation with
            { State = "pending", Diagnostic = "configuration_activation_unconfirmed" } } };
        var incompatible = saved is not null && contract is not null && (saved.Value.Definition.OptionSet != contract.Id
            || saved.Value.Definition.Version != contract.Version || saved.Value.Definition.SchemaDigest != contract.SchemaDigest);
        if (incompatible) saved = saved! with { Value = saved.Value with { Observation = saved.Value.Observation with
            { State = "incompatible", Diagnostic = "configuration_contract_changed" } } };
        return new(state is not null, contract is null ? null : new(contract.Id, contract.Version, contract.SchemaDigest,
                contract.Options.Select(o => new ExtensionConfigurationOption(o.Id, o.Schema, o.Required, o.Protection == AepValueProtection.Secured,
                    o.AllowedSources.Select(s => s.ToString()).ToArray(), o.Scope.ToString(), o.ApplyMode.ToString(), o.AtomicGroup)).ToArray()),
            saved?.Value, saved?.ETag, !incompatible && !outOfSync && state?.Ready == true,
            (state?.AppliedValues ?? new Dictionary<string, JsonElement>()).Where(p => contract!.Options.Any(o => o.Id == p.Key && o.Protection != AepValueProtection.Secured))
                .ToDictionary(p => p.Key, p => p.Value));
    }
    private static ExtensionConfigurationException Error(string code) => new(code);
    public void Dispose() { foreach (var gate in gates) gate.Dispose(); }
    private sealed class ResolvedValues(IReadOnlyList<AepBoundValue> values, string revision, Action dispose) : IDisposable
    {
        public IReadOnlyList<AepBoundValue> Values { get; } = values;
        public string Revision { get; } = revision;
        public void Dispose() => dispose();
    }
}

internal sealed class ConfigurationAccessTokenProvider(ExtensionRegistrationResource registration, ISecretResolver secrets,
    IRequestContextScopeFactory scopes) : IAepAccessTokenProvider
{
    public async ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var credential = registration.Definition.Credential ?? throw new ExtensionConfigurationException("configuration_credential_missing");
        using var owner = scopes.PushSystem();
        using var value = await secrets.ResolveAsync(new(credential.Resolve(registration.Namespace, SecretResourceKinds.Secret), credential.ScopeRef),
            new(ResourceScopeRef.Instance, registration.Address), cancellationToken)
            ?? throw new ExtensionConfigurationException("configuration_credential_unavailable");
        var token = new System.Text.UTF8Encoding(false, true).GetString(value.Value.AccessValue().Span);
        if (string.IsNullOrWhiteSpace(token) || token.Contains('\r') || token.Contains('\n')) throw new ExtensionConfigurationException("configuration_credential_invalid");
        return token;
    }
}
