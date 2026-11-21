using System.Net;
using Agentstration.Agents;
using Agentstration.Identity.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Console;
using Microsoft.AspNetCore.Components;

namespace Agentstration.Web.Components;

public partial class AgentResourcePreview
{
    [Parameter, EditorRequired] public ScopedResourceAddress Address { get; set; }
    [Parameter] public ResourcePreviewExpectation? Expectation { get; set; }
    [Parameter, EditorRequired] public string OverviewHref { get; set; } = string.Empty;
    [Parameter, EditorRequired] public string DefinitionHref { get; set; } = string.Empty;
    [Parameter, EditorRequired] public string RunHref { get; set; } = string.Empty;

    private AgentResource? resource;
    private ResourceLoadState loadState = new(ResourceLoadStatus.Loading);
    private CancellationTokenSource? pending;
    private string? requestedKey;
    private long revision;
    private bool disposed;
    private string SelectionKey => $"{Context.Current?.UserId:D}|{Context.Current?.SelectedScopeRef}|{(Address.ScopeRef == default ? string.Empty : Address.ScopeRef.Value)}|{Address.Namespace}|{Address.Kind}|{Address.Name}|{Expectation}";
    private bool CanEdit => resource is not null && resource.Namespace.IsDefault && !resource.Governance.IsManaged
        && resource.ScopeRef?.Value == Context.Current?.SelectedScopeRef && Context.HasPermission(AuthorizationPermissions.ResourcesWrite);

    protected override void OnInitialized() => Context.Changed += ContextChanged;
    private void ContextChanged() => _ = InvokeAsync(async () => { if (disposed) return; await LoadAsync(); if (!disposed) StateHasChanged(); });

    protected override Task OnParametersSetAsync()
    {
        if (requestedKey == SelectionKey) return Task.CompletedTask;
        return LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (disposed) return;
        var target = Address;
        var key = requestedKey = SelectionKey;
        var requestRevision = ++revision;
        pending?.Cancel();
        pending?.Dispose();
        pending = new CancellationTokenSource();
        var token = pending.Token;
        resource = null;
        loadState = new(ResourceLoadStatus.Loading);
        if (Context.Error is not null || Expectation?.CanRead(Context) == false || !Context.HasPermission(AuthorizationPermissions.ResourcesRead)) { loadState = new(ResourceLoadStatus.Forbidden); return; }
        if (target.Kind != AgentResourceKinds.Agent || target.ScopeRef == default) { loadState = new(ResourceLoadStatus.Failed); return; }
        try
        {
            var snapshot = await Client.GetAgentAsync(target.Namespace, target.Name, target.ScopeRef, token);
            if (!IsCurrent(requestRevision, key)) return;
            if (snapshot.Value.ScopeRef != target.ScopeRef || snapshot.Value.Namespace != target.Namespace || snapshot.Value.Name != target.Name)
            {
                loadState = new(ResourceLoadStatus.Forbidden);
                return;
            }
            if (Expectation?.Matches(snapshot.Value.Uid, snapshot.Value.Generation, snapshot.ETag) == false)
            { loadState = new(ResourceLoadStatus.NotFound); return; }
            resource = snapshot.Value;
            loadState = new(ResourceLoadStatus.Ready) { HasContent = true };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is HttpRequestException or AgentstrationApiException or OperationCanceledException)
        {
            if (!IsCurrent(requestRevision, key)) return;
            var status = exception is AgentstrationApiException api ? api.StatusCode : (exception as HttpRequestException)?.StatusCode;
            loadState = new(status switch
            {
                HttpStatusCode.NotFound => ResourceLoadStatus.NotFound,
                HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => ResourceLoadStatus.Forbidden,
                _ => ResourceLoadStatus.Failed
            })
            { ErrorId = (exception as AgentstrationApiException)?.ErrorId };
        }
    }

    private bool IsCurrent(long requestRevision, string key) => !disposed && revision == requestRevision
        && key == SelectionKey && Context.Error is null && Expectation?.CanRead(Context) != false && Context.HasPermission(AuthorizationPermissions.ResourcesRead);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        revision++;
        pending?.Cancel();
        pending?.Dispose();
        Context.Changed -= ContextChanged;
        GC.SuppressFinalize(this);
    }
}
