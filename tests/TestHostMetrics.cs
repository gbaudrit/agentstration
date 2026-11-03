using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agentstration.Tests;

// Opt-in measurements only: no requests, credentials or resource contents.
internal static class TestHostMetrics
{
    private static readonly object OutputLock = new();

    public static IHost Start(string module, Func<IHost> create)
    {
        var started = Stopwatch.GetTimestamp();
        var host = create();
        var ready = Stopwatch.GetTimestamp();
        var directory = Environment.GetEnvironmentVariable("AGENTSTRATION_TEST_HOST_METRICS");
        if (string.IsNullOrWhiteSpace(directory)) return host;
        var stopping = ready;
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Register(() => stopping = Stopwatch.GetTimestamp());
        lifetime.ApplicationStopped.Register(() =>
        {
            using var process = Process.GetCurrentProcess();
            var record = JsonSerializer.Serialize(new
            {
                module,
                startupMilliseconds = Stopwatch.GetElapsedTime(started, ready).TotalMilliseconds,
                activeMilliseconds = Stopwatch.GetElapsedTime(ready, stopping).TotalMilliseconds,
                shutdownMilliseconds = Stopwatch.GetElapsedTime(stopping).TotalMilliseconds,
                processPeakWorkingSetBytes = process.PeakWorkingSet64,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription
            });
            lock (OutputLock)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, $"{module}.jsonl"), record + Environment.NewLine);
            }
        });
        return host;
    }
}
