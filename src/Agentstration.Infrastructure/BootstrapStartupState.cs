namespace Agentstration.Infrastructure;

public sealed class BootstrapStartupState
{
    private TaskCompletionSource? completion;
    public bool IsReady => completion is null || completion.Task.IsCompletedSuccessfully;
    public string? Failure { get; private set; }

    public void Begin() => completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Complete() => completion?.TrySetResult();
    public void Fail(string reason)
    {
        Failure = reason;
        completion?.TrySetCanceled();
    }
    public Task WaitUntilReadyAsync(CancellationToken cancellationToken) =>
        completion?.Task.WaitAsync(cancellationToken) ?? Task.CompletedTask;
}
