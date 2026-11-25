using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Agentstration.Runtime.Worker.Coding;

internal sealed class CodingPrerequisiteCheck(IOptions<CodingWorkerOptions> options, TimeProvider clock) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var start = new ProcessStartInfo(options.Value.BubblewrapExecutable);
        foreach (var argument in new[] { "--unshare-user", "--unshare-pid", "--ro-bind", "/", "/", "--", "/usr/bin/true" }) start.ArgumentList.Add(argument);
        var result = await BoundedProcess.RunAsync(start, null, 4096, null, stopping.Token);
        if (result.ExitCode != 0) throw new InvalidOperationException("coding_linux_isolation_unavailable");
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
