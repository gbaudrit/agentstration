using Agentstration.Schemas;

namespace Agentstration.Infrastructure.Schemas;

/// <summary>Built-in materialization must not discover optional remote format providers.</summary>
public sealed class StartupSchemaFormatHandlerSource(ISchemaFormatHandlerSource source, BootstrapStartupState startup)
    : ISchemaFormatHandlerSource
{
    public ValueTask<IReadOnlyList<ISchemaFormatHandler>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = startup.Read();
        return !snapshot.Ready && snapshot.ActiveStepId is "topology" or "workers" or "builtins" or "finalization"
            ? ValueTask.FromResult<IReadOnlyList<ISchemaFormatHandler>>([])
            : source.ListAsync(cancellationToken);
    }
}
