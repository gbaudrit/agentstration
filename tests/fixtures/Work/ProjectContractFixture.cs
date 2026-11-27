using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Afc.Abstractions;
using Agentstration.Afc.Aep;
using Agentstration.Application.Work;
using Agentstration.Atc.Abstractions;
using Agentstration.Atc.Aep;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Extensions.Contracts;
using Agentstration.Identity;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure.Work;
using Agentstration.Parameters;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;
using Agentstration.Tools;
using Agentstration.Tools.Mcp;
using Agentstration.Work;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentstration.Tests.Work;

internal sealed class ProjectContractFixture : IResourceStore, IResourceIdentityCatalog, IResourceScopeResolver,
    IResourceAccessPolicyService, Agentstration.Identity.Contracts.IAuthorizationService, IWorkplaceContext,
    IHttpMessageHandlerFactory, IToolInvoker, IParameterResolver
{
    public WorkspaceId WorkspaceId { get; } = WorkspaceId.New();
    public Guid PrincipalId => Caller.PrincipalId;
    public RequestContext Caller { get; }
    public CurrentRequestContext Context { get; } = new();
    public Dictionary<ScopedResourceAddress, Resource> Resources { get; } = [];
    public Dictionary<string, AepManifest> Manifests { get; } = [];
    public HashSet<Guid> Denied { get; } = [];
    public bool Allowed { get; set; } = true;
    public HashSet<string> DeniedPermissions { get; } = [];
    public string ImplementationVersion { get; set; } = "1.0.0";
    public bool ForeignTarget { get; set; }
    public bool ReadOnly { get; set; }
    public bool UseActualMcp { get; set; }
    public Exception? InvocationError { get; private set; }
    public HttpMessageHandler? McpHandler { get; set; }
    public Action? BeforeInvoke { get; set; }
    public Action? BeforeList { get; set; }
    public int DiscoveryCalls { get; private set; }
    public int MetadataReads { get; private set; }
    public List<ToolExecutionContext> Calls { get; } = [];
    public Dictionary<ParameterReference, JsonElement> ParameterValues { get; } = [];
    public WorkProjectBindingResolver Resolver { get; }
    public WorkProjectContextService Contexts { get; }

    public ProjectContractFixture()
    {
        Caller = new(Guid.NewGuid(), Guid.NewGuid(), WorkspaceId.Value);
        var contracts = new WorkProjectContractResolver(this, new ResourceAepExtensionRegistrationResolver(this),
            new AepMcpTransport(this, NullLoggerFactory.Instance), new ToolExecutionPipeline(this), TimeProvider.System);
        var inspector = new ConnectedServiceManagementService(this, null!, new DescendantResourceUseAuthorizer(this), [], this, null!, this, TimeProvider.System, Context);
        var connections = new WorkProjectConnectedServiceResolver(this, inspector, this, Context);
        Resolver = new(this, this, new DescendantResourceUseAuthorizer(this), Context, Context, this, contracts, connections);
        Contexts = new(null!, Resolver, this, TimeProvider.System);
    }

    public WorkProjectContextBinding AddConnection(bool tracking, string name, ResourceScopeRef? owner = null)
    {
        var scope = owner ?? ResourceScopeRef.Workspace(WorkspaceId.Value);
        var extension = new ExtensionRegistrationResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ExtensionKinds.ExtensionRegistration,
            ScopeRef = scope,
            Uid = Guid.NewGuid(),
            Generation = 1,
            Metadata = new() { Name = name + "-extension" },
            Definition = new() { DisplayName = name, Endpoint = new("http://" + name + ".test/"), ExpectedExtensionId = "fixture." + name }
        };
        var provider = new ToolProviderResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ToolResourceKinds.ToolProvider,
            ScopeRef = scope,
            Uid = Guid.NewGuid(),
            Generation = 1,
            Metadata = new() { Name = name },
            Definition = new()
            {
                DisplayName = name,
                ProviderType = ToolProviderType.Aep,
                Aep = new() { Extension = new(extension.Name, scope, extension.Namespace) }
            }
        };
        Put(extension); Put(provider);
        var contributions = tracking ? AtcAepContributions.Create(name, "main") : AfcAepContributions.Create(name, "main");
        var manifest = new AepManifest(AepProtocol.Version, new("fixture." + name, name, "1.0.0"),
            new Dictionary<string, AepCapabilityDescriptor>(), new([], contributions), new([new("main", "/mcp")]));
        Manifests[extension.Definition.Endpoint.Host] = manifest;
        var operations = tracking ? AtcContract.Tools : AfcContract.Tools;
        foreach (var operation in operations)
        {
            var contribution = contributions.Single(value => value.Id == name + "." + operation);
            Put(new ToolResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ToolResourceKinds.Tool,
                ScopeRef = scope,
                Uid = Guid.NewGuid(),
                Generation = 1,
                Metadata = new() { Name = name + "-" + operation },
                Definition = new()
                {
                    DisplayName = operation,
                    Provider = new(provider.Name, scope, provider.Namespace),
                    ExternalId = contribution.Id,
                    Metadata = contribution.Metadata!,
                    Discovery = new() { Available = true, FirstSeenAt = DateTimeOffset.UnixEpoch, LastSeenAt = DateTimeOffset.UnixEpoch },
                    Schema = new() { Input = tracking ? AtcSchemas.Input(operation) : AfcSchemas.Input(operation), Output = tracking ? AtcSchemas.Output(operation) : AfcSchemas.Output(operation) }
                }
            });
        }
        var selected = Resources.Values.OfType<ToolResource>().Single(tool => tool.Name == name + "-describe");
        return new(name, tracking ? WorkProjectBindingPurpose.Tracking : WorkProjectBindingPurpose.Forge, Reference(selected))
        {
            Contract = new(Reference(provider), Reference(extension), "1.0", name + "-authority", "target", "binding", "revision-1", [tracking ? "list" : "resolve"])
        };
    }

    public WorkProjectConnectionSelection AddService(WorkProjectContextBinding route, string? name = null,
        bool instanceService = false, bool ancestorType = false, bool parameterRevision = false,
        string? providerId = null, string? targetId = null)
    {
        var scope = instanceService ? ResourceScopeRef.Instance : ResourceScopeRef.Workspace(WorkspaceId.Value);
        var typeScope = ancestorType ? ResourceScopeRef.Instance : scope;
        var schema = JsonSerializer.SerializeToElement(new { type = "object", additionalProperties = false });
        var names = WorkProjectConnectionRules.TargetFields;
        var version = new ConnectedServiceTypeVersionDescriptor("1.0.0", AepSchemaDigest.Compute(schema), schema,
            names.Select(value => new ConnectedServiceInputDescriptor(value, true, ConnectedServiceValueType.Text, ConnectedServiceValueProtection.Standard)).ToArray(),
            names.Select(value => new ConnectedServiceExportDescriptor(value, ConnectedServiceValueType.Text, ConnectedServiceValueProtection.Standard, SourceInputId: value)).ToArray());
        var grants = new DescendantUsePolicy { Grants = [new(ResourceScopeRef.Workspace(WorkspaceId.Value))] };
        var type = Resources.Values.OfType<ConnectedServiceTypeResource>().SingleOrDefault(value => value.ScopeRef == typeScope && value.Name == route.Name + "-type")
            ?? new ConnectedServiceTypeResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ConnectedServiceResourceKinds.ConnectedServiceType,
                Uid = Guid.NewGuid(),
                Generation = 1,
                ScopeRef = typeScope,
                Metadata = new() { Name = route.Name + "-type" },
                Definition = new() { DisplayName = "Contract target", PreferredVersion = version.Version, Versions = [version], UsePolicy = grants }
            };
        Put(type);
        var selection = route.Contract!;
        var fields = new Dictionary<string, string>
        {
            ["providerId"] = providerId ?? selection.ProviderId,
            ["targetId"] = targetId ?? selection.TargetId,
            ["bindingId"] = selection.BindingId,
            ["revision"] = selection.Revision
        };
        var bindings = fields.Select(value => new ConnectedServiceValueBinding
        { InputId = value.Key, Kind = ConnectedServiceBindingKind.Literal, Literal = JsonSerializer.SerializeToElement(value.Value) }).ToArray();
        if (parameterRevision)
        {
            var parameter = new ParameterResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = ParameterResourceKinds.Parameter,
                ScopeRef = scope,
                Uid = Guid.NewGuid(),
                Generation = 1,
                Metadata = new() { Name = route.Name + "-revision" },
                Definition = new() { DisplayName = "Revision", ValueType = ParameterValueType.Text, Value = JsonSerializer.SerializeToElement(selection.Revision) }
            };
            Put(parameter); var reference = new ParameterReference(parameter.Address, scope); ParameterValues[reference] = parameter.Definition.Value;
            bindings[3] = new() { InputId = "revision", Kind = ConnectedServiceBindingKind.Parameter, Parameter = reference };
        }
        var service = new ConnectedServiceResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ConnectedServiceResourceKinds.ConnectedService,
            Uid = Guid.NewGuid(),
            Generation = 1,
            ScopeRef = scope,
            Metadata = new() { Name = (name ?? route.Name) + "-service" },
            Definition = new()
            {
                DisplayName = "Configured contract target",
                Type = new()
                {
                    Type = new(type.Name, typeScope, type.Namespace),
                    TypeUid = type.Uid,
                    Version = version.Version,
                    SchemaDigest = version.SchemaDigest
                },
                Options = JsonSerializer.SerializeToElement(new { }),
                Bindings = bindings,
                UsePolicy = grants
            },
            ObservedState = new() { Availability = ConnectedServiceAvailability.Ready, ObservedAt = DateTimeOffset.UnixEpoch }
        };
        Put(service);
        return new(new(name ?? route.Name, route.Purpose == WorkProjectBindingPurpose.Tracking ? WorkProjectConnectionRules.Tracking : WorkProjectConnectionRules.Forge,
            "1.0", selection.RequiredOperations, new(fields["providerId"], fields["targetId"])),
            new(name ?? route.Name, Reference(service), route.Target, names.Select(value => new WorkProjectConnectionExport(value, value)).ToArray()));
    }

    public Task<ResolvedParameter?> ResolveAsync(ParameterReference parameter, ParameterResolutionContext context, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(ParameterValues.TryGetValue(parameter, out var value) ? new ResolvedParameter(parameter.Address, value) : null);
    }

    public void Put(Resource resource) => Resources[Reference(resource).Address] = resource;
    public static WorkProjectResourceReference Reference(Resource resource) => new(new(resource.ScopeRef!.Value, resource.Namespace, resource.Kind, resource.Name), resource.Uid, resource.Generation);
    public Task<Result<WorkProjectResolvedBinding>> ResolveAsync(WorkProjectContextBinding binding, CancellationToken token = default) => Resolver.ResolveAsync(WorkspaceId, PrincipalId, binding, token);

    public async ValueTask<JsonElement?> InvokeAsync(ToolExecutionContext invocation, CancellationToken token = default)
    {
        Assert.AreEqual(ControlPlaneAccessMode.Workspace, Context.AccessMode);
        Assert.IsNotNull(invocation.ExpectedProviderRouteDigest); Assert.IsNotNull(invocation.ExpectedToolSchemaDigest);
        Assert.IsFalse(invocation.PersistArguments); Calls.Add(invocation); BeforeInvoke?.Invoke();
        if (UseActualMcp)
        {
            var provider = Resources.Values.OfType<ToolProviderResource>().Single(value => value.Uid == invocation.ToolProviderUid);
            var tool = Resources.Values.OfType<ToolResource>().Single(value => value.Uid == invocation.ToolUid);
            var adapter = new ToolProviderAdapter(new ResourceAepExtensionRegistrationResolver(this),
                new ConfigurationToolProviderEnvironmentResolver(new ConfigurationBuilder().Build()), this, NullLoggerFactory.Instance);
            try { return await adapter.InvokeAsync(provider, tool, invocation.Arguments, token, invocation.ExpectedProviderRouteDigest, invocation.ExpectedToolSchemaDigest, invocation.ExpectedProviderToolContracts, invocation.BeforeProviderInvoke, invocation.ResolveProviderHeaders); }
            catch (Exception error) { InvocationError = error; throw; }
        }
        using var protectedHeaders = invocation.ResolveProviderHeaders is null ? null : await invocation.ResolveProviderHeaders(token);
        if (invocation.BeforeProviderInvoke is not null) await invocation.BeforeProviderInvoke(token);
        var input = invocation.Arguments!.Value;
        var id = input.GetProperty("arguments").GetProperty("requestId").GetString()!;
        var target = input.GetProperty("target");
        object response;
        if (invocation.ToolName.StartsWith("tracking", StringComparison.Ordinal))
        {
            var reference = target.Deserialize<AtcTargetRef>(AtcContract.Json)!;
            if (ForeignTarget) reference = reference with { TargetId = "foreign" };
            var capabilities = new AtcCapabilities(ReadOnly ? ["list", "read"] : AtcContract.Operations, ReadOnly ? [] : ["title"], ReadOnly ? [] : [AtcRelationKind.Related], AtcConcurrency.Observation, AtcRecoveryScope.None, 0, new());
            response = new AtcReply<AtcDescription>(AtcContract.Id, "1.0", id, new(AtcSchemas.Declaration(), reference, "fixture-tracker", ImplementationVersion, capabilities));
        }
        else
        {
            var reference = target.Deserialize<AfcTargetRef>(AfcContract.Json)!;
            if (ForeignTarget) reference = reference with { TargetId = "foreign" };
            var capabilities = new AfcCapabilities(AfcContract.Operations, AfcConcurrency.Observation, AfcRecoveryScope.None, 0, true, true, new());
            response = new AfcReply<AfcDescription>(AfcContract.Id, "1.0", id, new(AfcSchemas.Declaration(), reference, "fixture-forge", ImplementationVersion, capabilities));
        }
        return JsonSerializer.SerializeToElement(new { structuredContent = JsonSerializer.SerializeToElement(response, AtcContract.Json) });
    }

    public HttpMessageHandler CreateHandler(string name) => new Router(this);
    private sealed class Router(ProjectContractFixture fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.AreEqual(ControlPlaneAccessMode.Workspace, fixture.Context.AccessMode);
            if (request.RequestUri!.AbsolutePath == AepProtocol.DiscoveryPath)
            {
                fixture.DiscoveryCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(fixture.Manifests[request.RequestUri.Host], options: AepProtocol.JsonOptions) });
            }
            return new HttpMessageInvoker(fixture.McpHandler ?? throw new InvalidOperationException("Unexpected remote call."), false).SendAsync(request, token);
        }
    }

    public Task<ResourceIdentitySnapshot?> GetIdentityByUidAsync(Guid uid, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); MetadataReads++; Assert.AreEqual(ControlPlaneAccessMode.System, Context.AccessMode);
        var value = Resources.Values.SingleOrDefault(value => value.Uid == uid);
        return Task.FromResult(value is null ? null : new ResourceIdentitySnapshot(value.Uid, value.ScopeRef!.Value, value.Namespace, value.Kind, value.Name, value.Generation, null, "etag-" + value.Uid + "-" + value.Generation));
    }
    public Task<ResolvedResourceScope?> ResolveAsync(ResourceScopeRef scope, CancellationToken token) => Task.FromResult<ResolvedResourceScope?>(
        Scope(scope));
    private ResolvedResourceScope? Scope(ResourceScopeRef scope)
    {
        var instance = new ResourceScope(1, ResourceScopeRef.Instance, ResourceScopeKind.Instance, "instance", null);
        var tenant = new ResourceScope(2, ResourceScopeRef.Tenant(Caller.TenantId), ResourceScopeKind.Tenant, "tenant", 1);
        return scope == instance.Ref ? new(instance, []) : scope == tenant.Ref ? new(tenant, [instance])
            : scope == ResourceScopeRef.Workspace(WorkspaceId.Value) ? new(new(3, scope, ResourceScopeKind.Workspace, "workspace", 2), [tenant, instance]) : null;
    }
    public Task<ResourceAccessDecision> EvaluateAsync(ResourceAccessIdentity resource, ResourceScopeRef consumer, ResourceAccessAction action, CancellationToken token) =>
        Task.FromResult(new ResourceAccessDecision(!Denied.Contains(resource.Uid), "fixture_grant"));
    public Task<bool> HasPermissionAsync(RequestContext context, string permission, CancellationToken token) => Task.FromResult(Allowed && !DeniedPermissions.Contains(permission));
    public Task<StoredResource<T>?> GetExactAsync<T>(ScopedResourceAddress address, CancellationToken token) where T : Resource
    {
        token.ThrowIfCancellationRequested();
        Assert.IsTrue(Context.AccessMode == ControlPlaneAccessMode.Workspace || Context.AccessMode == ControlPlaneAccessMode.System
            && address.Kind is "ConnectedService" or "ConnectedServiceType" or "Parameter" or "Secret");
        return Task.FromResult(Resources.TryGetValue(address, out var value) ? new StoredResource<T>((T)value, "etag", DateTimeOffset.UnixEpoch) : null);
    }
    public Task<IReadOnlyList<StoredResource<T>>> ListExactAsync<T>(ResourceScopeRef scope, string kind, int skip, int take, CancellationToken token) where T : Resource
    {
        token.ThrowIfCancellationRequested(); BeforeList?.Invoke();
        Assert.AreEqual(ControlPlaneAccessMode.Workspace, Context.AccessMode);
        return Task.FromResult<IReadOnlyList<StoredResource<T>>>(Resources.Values.Where(value => value.ScopeRef == scope && value.Kind == kind).Cast<T>().Skip(skip).Take(take).Select(value => new StoredResource<T>(value, "etag", DateTimeOffset.UnixEpoch)).ToArray());
    }
    public Task<IReadOnlyList<ResourceInventoryEntry>> ListExactInventoryAsync(ResourceScopeRef scope, CancellationToken token) => throw new NotSupportedException();
    public Task<IReadOnlyList<ResourceAccessPolicyResponse>> ListAsync(ResourceScopeRef owner, CancellationToken token) => throw new NotSupportedException();
    public Task<ResourceAccessPolicyResponse?> GetAsync(Guid uid, CancellationToken token) => throw new NotSupportedException();
    public Task<ResourceAccessPolicyResponse> PutAsync(ResourceAccessPolicy policy, string? ifMatch, CancellationToken token) => throw new NotSupportedException();
    public Task DeleteAsync(Guid uid, string? ifMatch, CancellationToken token) => throw new NotSupportedException();
    public Task<IReadOnlySet<string>> GetPermissionsAsync(RequestContext context, CancellationToken token) => throw new NotSupportedException();
    public Task EnsurePermissionAsync(RequestContext context, string permission, CancellationToken token) => throw new NotSupportedException();
    public Task InitializeAsync(CancellationToken token) => Task.CompletedTask;
    public Task<StoredResource<T>?> GetAsync<T>(ResourceKey key, CancellationToken token) where T : Resource => throw new NotSupportedException();
    public Task<IReadOnlyList<StoredResource<T>>> ListAsync<T>(string kind, int skip, int take, CancellationToken token) where T : Resource => throw new NotSupportedException();
    public Task<StoredResource<T>> PutAsync<T>(T resource, string? ifMatch, bool ifNoneMatch, CancellationToken token) where T : Resource => throw new NotSupportedException();
    public Task<StoredResource<T>> PutExactAsync<T>(ResourceScopeRef scope, T resource, string? ifMatch, bool ifNoneMatch, CancellationToken token) where T : Resource => throw new NotSupportedException();
    public Task<StoredResource<T>> CreateImmutableAsync<T>(T resource, CancellationToken token) where T : Resource => throw new NotSupportedException();
    public Task DeleteAsync(ResourceKey key, string? ifMatch, CancellationToken token) => throw new NotSupportedException();
}
