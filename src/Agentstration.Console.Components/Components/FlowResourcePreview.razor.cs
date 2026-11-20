using System.Net;
using Agentstration.Flows;
using Agentstration.Flows.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Console;
using Microsoft.AspNetCore.Components;

namespace Agentstration.Web.Components;

public partial class FlowResourcePreview
{
    [Parameter, EditorRequired] public ScopedResourceAddress Address { get; set; }
    [Parameter, EditorRequired] public string OverviewHref { get; set; } = string.Empty;
    [Parameter, EditorRequired] public string DefinitionHref { get; set; } = string.Empty;
    [Parameter, EditorRequired] public string DesignerHref { get; set; } = string.Empty;
    private FlowResponse? resource;
    private ResourceLoadState loadState = new(ResourceLoadStatus.Loading);
    private CancellationTokenSource? pending;
    private string? requestedKey;
    private long revision;
    private bool disposed;
    private string SelectionKey => $"{Context.Current?.UserId:D}|{Context.Current?.SelectedScopeRef}|{(Address.ScopeRef == default ? string.Empty : Address.ScopeRef.Value)}|{Address.Namespace}|{Address.Kind}|{Address.Name}";
    private bool CanEdit => resource is not null && !resource.Governance.IsManaged && resource.ScopeRef?.Value == Context.Current?.SelectedScopeRef
        && Context.HasPermission(AuthorizationPermissions.ResourcesWrite);
    protected override Task OnParametersSetAsync() => requestedKey == SelectionKey ? Task.CompletedTask : LoadAsync();
    private async Task LoadAsync()
    {
        if (disposed) return;
        var target = Address;
        var key = requestedKey = SelectionKey;
        var requestRevision = ++revision;
        pending?.Cancel(); pending?.Dispose(); pending = new();
        var token = pending.Token;
        resource = null; loadState = new(ResourceLoadStatus.Loading);
        if (!Context.HasPermission(AuthorizationPermissions.ResourcesRead)) { loadState = new(ResourceLoadStatus.Forbidden); return; }
        if (target.Kind != FlowResourceKinds.Flow || target.ScopeRef == default) { loadState = new(ResourceLoadStatus.Failed); return; }
        try
        {
            var snapshot = await Client.GetFlowSnapshotAsync(target.Namespace, target.Name, target.ScopeRef, token);
            if (!IsCurrent(requestRevision, key)) return;
            if (snapshot.Value.ScopeRef != target.ScopeRef || snapshot.Value.Namespace != target.Namespace || snapshot.Value.Id != target.Name)
            { loadState = new(ResourceLoadStatus.Forbidden); return; }
            resource = snapshot.Value; loadState = new(ResourceLoadStatus.Ready) { HasContent = true };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or AgentstrationApiException or OperationCanceledException)
        {
            if (!IsCurrent(requestRevision, key)) return;
            var status = error is AgentstrationApiException api ? api.StatusCode : (error as HttpRequestException)?.StatusCode;
            loadState = new(status switch
            {
                HttpStatusCode.NotFound => ResourceLoadStatus.NotFound,
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ResourceLoadStatus.Forbidden,
                _ => ResourceLoadStatus.Failed
            })
            { ErrorId = (error as AgentstrationApiException)?.ErrorId };
        }
    }
    private bool IsCurrent(long requestRevision, string key) => !disposed && revision == requestRevision && key == SelectionKey && Context.HasPermission(AuthorizationPermissions.ResourcesRead);
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; revision++; pending?.Cancel(); pending?.Dispose(); GC.SuppressFinalize(this);
    }
}
