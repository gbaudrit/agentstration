using System.Text.Json;
using Agentstration.Afc.Abstractions;
using Agentstration.Application.Work;
using Agentstration.Coding;
using Agentstration.Coding.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.Resources;
using Agentstration.Runtime.Abstractions;
using Agentstration.Tools;
using Agentstration.Tools.Mcp;
using Agentstration.Work;
using ModelContextProtocol;

namespace Agentstration.Infrastructure.Work;

// AFC wire types and native states stop at this adapter. The durable delivery owns effect identity.
public sealed partial class AfcCodingForgeGateway(IWorkProjectBindingResolver bindings, IResourceStore resources,
    IToolExecutionPipeline pipeline, ICurrentRequestContext current, TimeProvider clock,
    WorkProjectConnectedServiceResolver services, ToolAccessService? toolAccess = null) : ICodingForgeGateway
{
    public async Task<Result<WorkForgeDeliveryRoute>> InspectAsync(WorkspaceId workspace, Guid principal,
        WorkProjectResolvedBinding connection, CodingRepositoryIdentity repository, CancellationToken token)
    {
        if (!Owns(workspace, principal)) return Result<WorkForgeDeliveryRoute>.Failure("coding_delivery_forge_denied");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            var allowed = await RevalidateAsync(workspace, principal, connection, linked.Token);
            if (!allowed.IsSuccess) return Result<WorkForgeDeliveryRoute>.Failure(allowed.ErrorCode!);
            var description = await DescribeAsync(connection, linked.Token);
            var caps = description.Capabilities;
            if (!new[] { "resolve", "read", "create" }.All(caps.Operations.Contains) || caps.Recovery == AfcRecoveryScope.None)
                return Result<WorkForgeDeliveryRoute>.Failure("coding_delivery_forge_recovery_unavailable");
            AfcResolveRequest? request = null;
            var observed = await CallAsync<AfcResolveRequest, AfcRepository>(connection, "resolve", id => request = new(id, [repository.DefaultBranch]),
                caps.Limits.MaximumResultBytes, linked.Token);
            AfcValidation.Repository(observed, Target(connection), request!, caps);
            if (observed.NativeId != repository.NativeId || observed.CloneUrl != repository.CloneUrl || observed.DefaultRef != repository.DefaultBranch
                || observed.Refs.Single().ObjectId != repository.Revision)
                return Result<WorkForgeDeliveryRoute>.Failure("coding_delivery_forge_repository_changed");
            return Result<WorkForgeDeliveryRoute>.Success(new(connection, caps.Recovery == AfcRecoveryScope.Durable ? WorkForgeRecovery.Durable : WorkForgeRecovery.Process,
                caps.ReceiptWindowSeconds, caps.Limits.MaximumResultBytes));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Result<WorkForgeDeliveryRoute>.Failure("coding_delivery_forge_timeout"); }
        catch (Exception error) when (Expected(error)) { return Result<WorkForgeDeliveryRoute>.Failure("coding_delivery_forge_unavailable"); }
    }

    public Result<string> Prepare(WorkTaskDeliveryIntent intent, WorkCodePublicationReceipt code)
    {
        try
        {
            if (intent.Forge is null || code.Status != WorkTaskDeliveryEffectStatus.Succeeded || !WorkTaskDeliveryFormat.Revision(code.Revision))
                return Result<string>.Failure("coding_delivery_forge_intent_invalid");
            return Result<string>.Success(AfcContract.Digest(Target(intent.Forge.Connection), Intent(intent, code)));
        }
        catch (Exception error) when (Expected(error)) { return Result<string>.Failure("coding_delivery_forge_intent_invalid"); }
    }

    public async Task<Result<WorkForgeDeliveryResponse>> InvokeAsync(WorkspaceId workspace, Guid principal,
        WorkTaskDeliveryOperation operation, string operationKey, bool recover, CancellationToken token)
    {
        if (!Owns(workspace, principal) || operation.WorkspaceId != workspace || operation.OwnerPrincipalId != principal)
            return Failure("coding_delivery_forge_denied");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            var route = operation.Intent.Forge!; var connection = route.Connection;
            var allowed = await RevalidateAsync(workspace, principal, connection, linked.Token);
            if (!allowed.IsSuccess) return Failure(allowed.ErrorCode!);
            var prepared = Prepare(operation.Intent, operation.Code!);
            if (!prepared.IsSuccess || prepared.Value != operation.ForgeWireIntentDigest) return Failure("coding_delivery_forge_intent_changed");
            var description = await DescribeAsync(connection, linked.Token); var caps = description.Capabilities;
            if (caps.ReceiptWindowSeconds != route.ReceiptWindowSeconds || caps.Limits.MaximumResultBytes != route.MaximumResultBytes
                || (caps.Recovery == AfcRecoveryScope.Durable ? WorkForgeRecovery.Durable : caps.Recovery == AfcRecoveryScope.Process ? WorkForgeRecovery.Process : WorkForgeRecovery.None) != route.Recovery)
                return Failure("coding_delivery_forge_route_changed");
            AfcCreateRequest? request = null;
            var receipt = await CallAsync<AfcCreateRequest, AfcReceipt>(connection, "create", id =>
            {
                request = new(id, operationKey, prepared.Value!, Intent(operation.Intent, operation.Code!), recover ? AfcMutationMode.Recover : AfcMutationMode.Execute);
                AfcValidation.CreateRequest(Target(connection), request, caps); return request;
            }, caps.Limits.MaximumResultBytes, linked.Token);
            AfcValidation.Receipt(receipt, Target(connection), request!);
            WorkPullRequestReceipt? review = null;
            if (receipt.Status == AfcReceiptStatus.Applied && receipt.ChangeRequest is { } item)
                review = new(operation.Intent.Provider, operation.Intent.Repository, item.Identity.NativeId, item.DisplayId, item.WebUrl,
                    item.Head.Name, item.Head.ObjectId, item.Base.Name, item.Base.ObjectId, item.Draft, item.Merged)
                {
                    Forge = new(item.Identity.ProviderId, item.Identity.TargetId),
                    Tracking = new(operation.Intent.Issue.Tracking!.ProviderId, operation.Intent.Issue.Tracking.TargetId, operation.Intent.Issue.TaskId)
                };
            return Result<WorkForgeDeliveryResponse>.Success(new(receipt.OperationKey, receipt.IntentDigest, receipt.Status switch
            {
                AfcReceiptStatus.Applied => WorkTaskDeliveryEffectStatus.Succeeded,
                AfcReceiptStatus.Rejected => WorkTaskDeliveryEffectStatus.Rejected,
                AfcReceiptStatus.Missing => WorkTaskDeliveryEffectStatus.Missing,
                _ => WorkTaskDeliveryEffectStatus.Uncertain
            }, review, receipt.ErrorCode));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Failure("coding_delivery_forge_timeout"); }
        catch (ToolExecutionDeniedException) { return Failure("coding_delivery_forge_denied"); }
        catch (Exception error) when (Expected(error)) { return Failure("coding_delivery_forge_uncertain"); }
    }

    private async Task<Result<WorkProjectResolvedBinding>> RevalidateAsync(WorkspaceId workspace, Guid principal, WorkProjectResolvedBinding connection, CancellationToken token)
    {
        var fresh = await bindings.ResolveAsync(workspace, principal, connection.Binding, token);
        return !fresh.IsSuccess ? fresh : WorkPlanDefinition.Digest(fresh.Value) == WorkPlanDefinition.Digest(connection)
            ? fresh : Result<WorkProjectResolvedBinding>.Failure("coding_delivery_forge_route_changed");
    }
    private async Task<AfcDescription> DescribeAsync(WorkProjectResolvedBinding connection, CancellationToken token)
    {
        var result = await CallAsync<AfcDescribeRequest, AfcDescription>(connection, "describe", id => new(id), 262_144, token);
        AfcSchemas.Validate(result.Declaration); AfcValidation.Capabilities(result.Capabilities);
        AfcValidation.Require(result.Target == Target(connection) && WorkPlanDefinition.Digest(result.Capabilities) == connection.Contract!.CapabilitiesDigest,
            "connection_changed"); return result;
    }
    private static AfcCreateIntent Intent(WorkTaskDeliveryIntent intent, WorkCodePublicationReceipt code) => new(intent.RepositoryNativeId, intent.Title,
        new(intent.HeadRef["refs/heads/".Length..], code.Revision!), new(intent.BaseBranch, intent.BaseRevision), true, intent.Body,
        [new(intent.Issue.Tracking!.ProviderId, intent.Issue.Tracking.TargetId, intent.Issue.TaskId)]);
    private static AfcTargetRef Target(WorkProjectResolvedBinding connection) => new(connection.Binding.Contract!.ProviderId, connection.Binding.Contract.TargetId,
        connection.Binding.Contract.BindingId, connection.Binding.Contract.Revision);
    private bool Owns(WorkspaceId workspace, Guid principal) => workspace.Value == current.Current.WorkspaceId && principal == current.Current.PrincipalId;
    private static bool Expected(Exception error) => error is AfcException or JsonException or ToolExecutionDeniedException
        or ToolInputValidationException or ToolResolutionException or ToolDefinitionInvocationException or McpException or HttpRequestException;
    private static Result<WorkForgeDeliveryResponse> Failure(string code) => Result<WorkForgeDeliveryResponse>.Failure(code);
}
