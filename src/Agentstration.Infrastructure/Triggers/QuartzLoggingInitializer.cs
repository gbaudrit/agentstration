using Microsoft.Extensions.Hosting;
using Quartz.Logging;

namespace Agentstration.Infrastructure.Triggers;

public sealed class QuartzLoggingInitializer : IHostedService
{
    public QuartzLoggingInitializer()
    {
        // Quartz 3 keeps its logging provider in static process state. Reset stale providers
        // before another in-process host (notably WebApplicationFactory) constructs Quartz.
        LogProvider.SetCurrentLogProvider(NullQuartzLogProvider.Instance);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private sealed class NullQuartzLogProvider : ILogProvider
    {
        public static NullQuartzLogProvider Instance { get; } = new();
        public Logger GetLogger(string name) => static (level, message, exception, parameters) => false;
        public IDisposable OpenNestedContext(string message) => EmptyDisposable.Instance;
        public IDisposable OpenMappedContext(string key, object value, bool destructure = false) => EmptyDisposable.Instance;

        private sealed class EmptyDisposable : IDisposable
        {
            public static EmptyDisposable Instance { get; } = new();
            public void Dispose() { }
        }
    }
}
