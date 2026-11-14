using Agentstration.Web.FlowDesigner.Backend;

namespace Agentstration.Web.FlowDesigner.Components;

public partial class FlowDesigner
{
    private readonly SemaphoreSlim saveGate = new(1, 1);
    private CancellationTokenSource? targetCancellation;
    private bool loadingTarget;

    // Capture the target before queueing. Debouncing cancels queued work, never a write
    // already in progress; changing Flow cancels the entire old editing session.
    private async Task WithSavedDraftAsync(
        Func<FlowDesignerTarget, CancellationToken, Task>? action = null,
        bool includeSource = true,
        CancellationToken queuedCancellation = default)
    {
        if (ReadOnly || Editor.State.Resource is null || Editor.State.ETag is null) return;
        var target = Target;
        var token = targetCancellation!.Token;
        using var queued = CancellationTokenSource.CreateLinkedTokenSource(token, queuedCancellation);
        var acquired = false;
        try
        {
            await saveGate.WaitAsync(queued.Token);
            acquired = true;
            queued.Token.ThrowIfCancellationRequested();
            saving = true;
            error = null;
            if (!await SavePendingChangesAsync(target, includeSource, token)) return;
            if (action is not null) await action(target, token);
        }
        catch (OperationCanceledException) when (queued.IsCancellationRequested) { }
        catch (FlowDesignerVersionAlreadyPublishedException exception)
        {
            if (!token.IsCancellationRequested) error = T("PublishVersionExists", exception.Version);
        }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested) error = exception.Message;
        }
        finally
        {
            if (acquired)
            {
                if (!token.IsCancellationRequested) saving = false;
                saveGate.Release();
            }
        }
    }

    private async Task<bool> SavePendingChangesAsync(FlowDesignerTarget target, bool includeSource, CancellationToken token)
    {
        while (Editor.State.IsDirty)
        {
            token.ThrowIfCancellationRequested();
            var snapshot = Editor.CaptureSave();
            // Typing YAML is a local buffer until the user explicitly saves/applies it.
            if (snapshot.HasSourceChanges && !includeSource) return false;
            Editor.MarkSaving();
            try
            {
                var resource = snapshot.Resource;
                var saved = snapshot.HasSourceChanges
                    ? await Backend.ReplaceSourceAsync(target, new(snapshot.Source), snapshot.ETag, token)
                    : await Backend.SaveDraftAsync(target, new(resource.DisplayName, resource.Description, resource.Tags, resource.Definition), snapshot.ETag, token);
                token.ThrowIfCancellationRequested();
                if (!Editor.AcknowledgeSave(saved, snapshot)) return false;
                // Retain the acknowledged ETag even if the following source read fails.
                var canonical = await Backend.GetSourceAsync(target, token);
                token.ThrowIfCancellationRequested();
                if (canonical.Revision != saved.Value.Revision)
                    throw new InvalidOperationException(T("DraftChangedDuringSave"));
                if (!Editor.MarkSaved(saved, canonical.Source, snapshot)) return false;
                if (snapshot.HasSourceChanges)
                {
                    await RefreshFlowCallOutputsAsync(Editor.State.Resource!.Definition);
                    token.ThrowIfCancellationRequested();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                if (!token.IsCancellationRequested && Editor.IsCurrent(snapshot))
                {
                    error = exception.Message;
                    Editor.MarkSaveFailed();
                    if (snapshot.HasSourceChanges && Editor.CaptureSave().SourceRevision == snapshot.SourceRevision)
                        Editor.SetSourceError(exception.Message);
                }
                return false;
            }
        }
        return true;
    }
}
