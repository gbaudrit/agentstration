using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Aep.Client;

namespace Agentstration.Extensions.Foundry;

public sealed class FoundryBoundConnectionResolver(HttpClient secretAccessClient)
{
    public async ValueTask<FoundryConnectionLease> ResolveAsync(IReadOnlyList<AepBoundValue>? boundValues, CancellationToken cancellationToken = default)
    {
        var resolver = new AepValueResolver(boundValues, secretAccessClient);
        var resolved = new List<AepResolvedValue>();
        try
        {
            var project = await ResolveRequiredAsync(resolver, FoundryValueRequirements.ProjectEndpoint, resolved, cancellationToken);
            var inference = await ResolveRequiredAsync(resolver, FoundryValueRequirements.InferenceEndpoint, resolved, cancellationToken);
            var modeText = await ResolveRequiredAsync(resolver, FoundryValueRequirements.AuthenticationMode, resolved, cancellationToken);
            if (!Enum.TryParse<FoundryAuthenticationMode>(modeText, out var mode) || !Enum.IsDefined(mode))
                throw new InvalidOperationException("authenticationMode is invalid.");
            var connection = new FoundryConnection
            {
                ProjectEndpoint = ParseUri(project),
                InferenceEndpoint = ParseUri(inference),
                AuthenticationMode = mode,
                Credential = await ResolveOptionalAsync(resolver, FoundryValueRequirements.Credential, resolved, cancellationToken),
                ManagedIdentityClientId = await ResolveOptionalAsync(resolver, FoundryValueRequirements.ManagedIdentityClientId, resolved, cancellationToken),
                WorkloadIdentityTenantId = await ResolveOptionalAsync(resolver, FoundryValueRequirements.WorkloadIdentityTenantId, resolved, cancellationToken),
                WorkloadIdentityClientId = await ResolveOptionalAsync(resolver, FoundryValueRequirements.WorkloadIdentityClientId, resolved, cancellationToken),
                WorkloadIdentityTokenFile = await ResolveOptionalAsync(resolver, FoundryValueRequirements.WorkloadIdentityTokenFile, resolved, cancellationToken)
            };
            connection.Validate();
            return new FoundryConnectionLease(connection, resolved);
        }
        catch (AepProtocolException exception)
        {
            Dispose(resolved);
            throw new AepServerException(exception.Code, "The Foundry provider values are unavailable or invalid.", 422, exception);
        }
        catch (InvalidOperationException exception)
        {
            Dispose(resolved);
            throw new AepServerException("bound_value_invalid", "The Foundry provider values are unavailable or invalid.", 422, exception);
        }
        catch { Dispose(resolved); throw; }
    }

    private static async ValueTask<string> ResolveRequiredAsync(AepValueResolver resolver, string id, ICollection<AepResolvedValue> resolved, CancellationToken cancellationToken)
    {
        var value = await resolver.ResolveAsync(id, cancellationToken);
        resolved.Add(value);
        return value.ReadString();
    }
    private static async ValueTask<string?> ResolveOptionalAsync(AepValueResolver resolver, string id, ICollection<AepResolvedValue> resolved, CancellationToken cancellationToken) =>
        resolver.Contains(id) ? await ResolveRequiredAsync(resolver, id, resolved, cancellationToken) : null;
    private static Uri ParseUri(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        ? uri : throw new InvalidOperationException("A Foundry endpoint is invalid.");
    private static void Dispose(IEnumerable<AepResolvedValue> values) { foreach (var value in values) value.Dispose(); }
}

public sealed class FoundryConnectionLease(FoundryConnection connection, IReadOnlyList<AepResolvedValue> values) : IDisposable
{
    public FoundryConnection Connection { get; } = connection;
    public void Dispose() { foreach (var value in values) value.Dispose(); }
}

public static class FoundryValueRequirements
{
    private static readonly IReadOnlyList<JsonElement> AuthenticationModes =
        Enum.GetNames<FoundryAuthenticationMode>()
            .Select(value => JsonSerializer.SerializeToElement(value, AepProtocol.JsonOptions))
            .ToArray();

    public const string ProjectEndpoint = "projectEndpoint";
    public const string InferenceEndpoint = "inferenceEndpoint";
    public const string AuthenticationMode = "authenticationMode";
    public const string Credential = "credential";
    public const string ManagedIdentityClientId = "managedIdentityClientId";
    public const string WorkloadIdentityTenantId = "workloadIdentityTenantId";
    public const string WorkloadIdentityClientId = "workloadIdentityClientId";
    public const string WorkloadIdentityTokenFile = "workloadIdentityTokenFile";

    public static IReadOnlyList<AepValueRequirement> All { get; } =
    [
        Standard(ProjectEndpoint, true, "uri"), Standard(InferenceEndpoint, true, "uri"),
        Standard(AuthenticationMode, true, allowedValues: AuthenticationModes),
        new(AepContributionKinds.ModelProvider, "microsoft-foundry", Credential, false, AepValueType.Text, AepValueProtection.Secured),
        Standard(ManagedIdentityClientId, false, "uuid"), Standard(WorkloadIdentityTenantId, false, "uuid"),
        Standard(WorkloadIdentityClientId, false, "uuid"), Standard(WorkloadIdentityTokenFile, false)
    ];

    private static AepValueRequirement Standard(
        string id,
        bool required,
        string? format = null,
        IReadOnlyList<JsonElement>? allowedValues = null) =>
        new(AepContributionKinds.ModelProvider, "microsoft-foundry", id, required, AepValueType.Text,
            AepValueProtection.Standard, format, AllowedValues: allowedValues);
}
