using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentstration.ConnectedServices;
using Agentstration.ConnectedServices.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Runtime.Core;
using Microsoft.Extensions.Configuration;

namespace Agentstration.Infrastructure.Bootstrap;

public sealed class ConnectedServiceDiscoveryBootstrapHandler(
    BootstrapResourcePreparation preparation,
    ConnectedServiceManagementService services,
    ConnectedServiceOperationDiscoveryService discoveries,
    ConnectedServiceOperationService operations,
    IOperationalTopologyProvisioner topology,
    IRequestContextScopeFactory scopes,
    IConfiguration configuration,
    TimeProvider timeProvider,
    RuntimeWorkerAssignmentService assignments,
    RuntimeAssignmentAvailabilitySignal availability,
    BootstrapStartupState startup) : IBootstrapResourceHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public string Kind => "ConnectedServiceOperationDiscovery";
    public BootstrapProfileScope Scope => BootstrapProfileScope.Instance;
    public bool SupportsProfileScope(BootstrapProfileScope profileScope) => true;

    public async Task<BootstrapResourcePlanResult> PlanAsync(BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation, BootstrapPlanningContext planning, CancellationToken cancellationToken)
    {
        var definition = Read(resource);
        var scope = DataSourceProfileBootstrapResourceHandler.TargetScope(operation);
        var reference = definition.ConnectedService.ResourceRef;
        var ns = reference.Namespace ?? resource.Metadata.Namespace;
        if (await services.GetExactAsync(reference.ScopeRef ?? scope, ns, reference.Name, cancellationToken) is null
            && ((reference.ScopeRef ?? scope) != scope || !planning.Contains("ConnectedService", reference.Name, ns.Value)))
            throw new InvalidOperationException("bootstrap_discovery_service_not_planned");
        var complete = true;
        foreach (var output in definition.Operations)
        {
            var selected = await preparation.ResolveOutputAsync(resource, operation, output.Name, cancellationToken);
            if (selected is null) complete = false;
            else if (output.Enabled && !selected.Enabled && selected.Generation > 1)
                throw new InvalidOperationException("bootstrap_operation_disabled_by_operator");
            else complete &= selected.Enabled == output.Enabled;
        }
        planning.Register(Kind, resource.Metadata.Name, resource.Metadata.Namespace.Value, resource);
        return new(complete ? BootstrapResourceDisposition.Skip : BootstrapResourceDisposition.Create);
    }

    public async Task<BootstrapResourceApplyResult> ApplyAsync(BootstrapResourceDocument resource,
        BootstrapResourceOperationContext operation, CancellationToken cancellationToken)
    {
        var definition = Read(resource);
        var scope = DataSourceProfileBootstrapResourceHandler.TargetScope(operation);
        var reference = definition.ConnectedService.ResourceRef;
        var ns = reference.Namespace ?? resource.Metadata.Namespace;
        var selected = await SelectAsync();
        if (operation.ActorPrincipalId is not { } actor)
            throw new InvalidOperationException("bootstrap_discovery_actor_required");
        var workspace = operation.ProfileScope == BootstrapProfileScope.Instance
            ? await topology.GetInstanceOperationsWorkspaceAsync(cancellationToken)
            : operation.ProfileScope == BootstrapProfileScope.Tenant
                ? await topology.GetTenantOperationsWorkspaceAsync(operation.Target!.TenantId!.Value, cancellationToken) : null;
        var context = new RequestContext(actor, workspace?.TenantId ?? operation.Target!.TenantId!.Value,
            workspace?.Id ?? operation.Target!.WorkspaceId!.Value);
        using var execution = operation.ProfileScope == BootstrapProfileScope.Instance ? scopes.PushInstance(context)
            : operation.ProfileScope == BootstrapProfileScope.Tenant ? scopes.PushTenantAdministration(context) : scopes.Push(context);
        var changed = false;
        if (selected.Any(item => item is null))
        {
            var timeout = configuration.GetValue<TimeSpan?>("Agentstration:Bootstrap:DiscoveryTimeout") ?? TimeSpan.FromMinutes(10);
            if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromMinutes(30))
                throw new InvalidOperationException("bootstrap_discovery_timeout_invalid");
            using var deadline = new CancellationTokenSource(timeout, timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            var key = "bootstrap-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{operation.ProfileName}:{resource.Metadata.Namespace}:{resource.Metadata.Name}:{scope}"))).ToLowerInvariant();
            var previousFailures = (await discoveries.ListAsync(reference.ScopeRef ?? scope, ns, reference.Name, linked.Token))
                .Where(item => item.State is ConnectedServiceOperationDiscoveryState.Failed or ConnectedServiceOperationDiscoveryState.Cancelled or ConnectedServiceOperationDiscoveryState.TimedOut)
                .Select(item => item.IdempotencyKey).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var attemptKey = key;
            var retry = 0;
            // Skip only failures that existed before this invocation. A fresh failure
            // stops this application; an explicit later application may try again.
            while (previousFailures.Contains(attemptKey))
                attemptKey = key + "-retry-" + (++retry).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var discovery = (await discoveries.StartAsync(reference.ScopeRef ?? scope, ns, reference.Name, key, attemptKey, linked.Token)).Value;
            // The ordinary Flow queue has already created its durable root assignment.
            // Child Flows execute under that same assignment and its existing authorization.
            if (operation.StartupResourceId is not null && !startup.IsReady)
                await AdmitAsync(discovery.FlowRunId, context.WorkspaceId, linked.Token);
            try
            {
                while (discovery.State != ConnectedServiceOperationDiscoveryState.Succeeded)
                {
                    if (discovery.State is ConnectedServiceOperationDiscoveryState.Failed or ConnectedServiceOperationDiscoveryState.Cancelled or ConnectedServiceOperationDiscoveryState.TimedOut)
                        throw new InvalidOperationException("bootstrap_discovery_failed");
                    if (operation.ReportStage is { } report)
                        await report(discovery.State == ConnectedServiceOperationDiscoveryState.Pending ? "waiting_worker" : "discovery_running", discovery.FlowRunId, linked.Token);
                    await Task.Delay(TimeSpan.FromMilliseconds(500), timeProvider, linked.Token);
                    discovery = (await discoveries.GetAsync(ns, discovery.Name, linked.Token)).Value;
                }
                changed = true;
            }
            catch (OperationCanceledException)
            {
                using var cancelDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), timeProvider);
                try { _ = await discoveries.CancelAsync(ns, discovery.Name, cancelDeadline.Token); }
                catch (Exception exception) when (exception is OperationCanceledException or ConnectedServiceOperationDiscoveryException) { }
                if (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    throw new TimeoutException("bootstrap_discovery_timeout");
                throw;
            }
            selected = await SelectAsync();
        }
        if (operation.ReportStage is { } validating) await validating("discovery_validation", null, cancellationToken);
        for (var index = 0; index < selected.Length; index++)
        {
            var candidate = selected[index] ?? throw new InvalidOperationException("bootstrap_discovery_output_not_found");
            if (!candidate.Definition.Definition.TryGetProperty("readOnly", out var readOnly) || readOnly.ValueKind != JsonValueKind.True)
                throw new InvalidOperationException("bootstrap_discovery_output_not_read_only");
            if (definition.Operations[index].Enabled != candidate.Enabled)
            {
                if (candidate.Generation > 1) throw new InvalidOperationException("bootstrap_operation_modified_by_operator");
                var exact = await discoveries.GetOperationAsync(candidate.ScopeRef!.Value, candidate.Namespace, candidate.Name, cancellationToken)
                    ?? throw new InvalidOperationException("bootstrap_discovery_output_unavailable");
                _ = await operations.SetEnabledAsync(new(candidate.ScopeRef.Value, candidate.Namespace, candidate.Kind, candidate.Name), definition.Operations[index].Enabled, exact.ETag, cancellationToken);
                changed = true;
            }
        }
        return changed ? BootstrapResourceApplyResult.Created : BootstrapResourceApplyResult.Skipped;

        async Task<ConnectedServiceOperationResource?[]> SelectAsync()
        {
            var result = new List<ConnectedServiceOperationResource?>();
            foreach (var output in definition.Operations)
                result.Add(await preparation.ResolveOutputAsync(resource, operation, output.Name, cancellationToken));
            return result.ToArray();
        }

        async Task AdmitAsync(string runId, Guid workspaceId, CancellationToken token)
        {
            var attempt = startup.Read().AttemptId;
            var assignment = await assignments.GetByTargetExactAsync(ResourceScopeRef.Workspace(workspaceId), RuntimeAssignmentTargetKind.FlowRun, runId, token)
                ?? throw new InvalidOperationException("bootstrap_discovery_assignment_missing");
            startup.AdmitAssignment(attempt, assignment.Value.Id);
            startup.ConfigureDiscovery(operation.StartupResourceId!, assignment.Value.RuntimeCapability,
                timeProvider.GetUtcNow().Add(configuration.GetValue<TimeSpan?>("Agentstration:Bootstrap:DiscoveryTimeout") ?? TimeSpan.FromMinutes(10)));
            availability.Pulse();
        }
    }

    private static BootstrapDiscoveryDefinition Read(BootstrapResourceDocument resource)
    {
        var value = resource.Definition.Deserialize<BootstrapDiscoveryDefinition>(Json)
            ?? throw new InvalidOperationException("bootstrap_discovery_invalid");
        if (value.ConnectedService?.ResourceRef is not { Name.Length: > 0 } || value.Operations is not { Count: > 0 and <= 32 }
            || value.ConnectedService.ResourceRef.Kind is { } kind && kind != "ConnectedService"
            || value.Operations.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != value.Operations.Count
            || value.Operations.Any(item => string.IsNullOrWhiteSpace(item.Name) || item.Selector.Count is 0 or > 6
                || item.Selector.Any(pair => pair.Key is not ("operationId" or "method" or "path" or "endpoint" or "responseStatus" or "responseMediaType")
                    || string.IsNullOrWhiteSpace(pair.Value))))
            throw new InvalidOperationException("bootstrap_discovery_invalid");
        return value;
    }
}
