using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Extensions.Contracts;
using Agentstration.Parameters;
using Agentstration.Secrets.Abstractions;
using Agentstration.Tools;
using Agentstration.Tools.Mcp;
using Microsoft.Extensions.Configuration;

namespace Agentstration.Infrastructure.ConnectedServices;

public sealed class ConnectedServiceBoundValues(
    IParameterResolver parameters, ISecretCapabilityService capabilities, IConfiguration configuration)
{
    public async Task<JsonElement?> ExecuteAsync(ConnectedServiceResource service,
        ConnectedServiceTypeVersionDescriptor version, ExtensionRegistrationResource registration,
        AepManifest manifest, InternalMcpToolInvocation invocation,
        Func<IReadOnlyList<AepBoundValue>, Task<JsonElement?>> action, CancellationToken cancellationToken)
    {
        var handles = new List<SecretCapabilityHandle>();
        var values = new List<AepBoundValue>();
        try
        {
            var declared = version.Inputs.Select(input => input.Id).ToArray();
            foreach (var binding in service.Definition.Bindings)
            {
                if (binding.Kind == ConnectedServiceBindingKind.Literal)
                {
                    if (binding.Literal is not { ValueKind: not JsonValueKind.Undefined } literal
                        || !version.Inputs.Any(input => input.Id == binding.InputId
                            && input.Protection == ConnectedServiceValueProtection.Standard))
                        throw Error("connected_service_literal_binding_invalid");
                    values.Add(AepBoundValue.Inline(binding.InputId, literal.Clone()));
                }
                else if (binding.Kind == ConnectedServiceBindingKind.Parameter)
                {
                    var parameter = await parameters.ResolveAsync(binding.Parameter!,
                        new(service.ScopeRef!.Value, service.Address), cancellationToken)
                        ?? throw Error("connected_service_parameter_unavailable");
                    values.Add(AepBoundValue.Inline(binding.InputId, parameter.Value));
                }
                else if (binding.Kind == ConnectedServiceBindingKind.Secret)
                {
                    if (!manifest.Capabilities.TryGetValue(AepCapabilityNames.SecretAccess, out var access)
                        || access.Version != AepProtocol.SecretAccessVersion)
                        throw Error("connected_service_secret_access_unsupported");
                    if (!Uri.TryCreate(configuration["Agentstration:Aep:SecretAccess:PublicBaseUrl"], UriKind.Absolute, out var baseUri)
                        || baseUri.Scheme != "https" && !(baseUri.Scheme == "http" && baseUri.IsLoopback))
                        throw Error("connected_service_secret_callback_unavailable");
                    var context = new SecretCapabilityContext(
                        new(registration.ScopeRef!.Value, registration.Namespace, registration.Kind, registration.Name),
                        manifest.Extension.Id, new(service.ScopeRef!.Value, service.Namespace, service.Kind, service.Name),
                        binding.InputId, invocation.CallId);
                    var handle = await capabilities.IssueAsync(context, new(binding.InputId, binding.Secret!),
                        declared, cancellationToken, cancellationToken);
                    handles.Add(handle);
                    values.Add(AepBoundValue.Secured(binding.InputId, new(AepProtocol.SecretAccessVersion,
                        new Uri(baseUri, AepProtocol.SecretAccessPath), manifest.Extension.Id,
                        binding.InputId, invocation.CallId, handle.RevealForTransport())));
                }
                else throw Error("connected_service_binding_kind_invalid");
            }
            return await action(values);
        }
        finally
        {
            foreach (var handle in handles) capabilities.Revoke(handle);
        }
    }
    private static ConnectedServiceOperationDiscoveryException Error(string code) => new(code, "Connected Service bindings could not be resolved.");
}
