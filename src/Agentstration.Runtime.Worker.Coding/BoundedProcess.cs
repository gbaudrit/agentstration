using System.Diagnostics;
using System.Text;

namespace Agentstration.Runtime.Worker.Coding;

internal sealed record BoundedProcessResult(int ExitCode, string Output);
internal static class BoundedProcess
{
    internal static async Task<BoundedProcessResult> RunAsync(ProcessStartInfo start, string? input, int maximumOutputBytes,
        Func<string, CancellationToken, Task>? onLine, CancellationToken token)
    {
        start.UseShellExecute = false; start.CreateNoWindow = true;
        start.RedirectStandardInput = true; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var process = Process.Start(start) ?? throw new CodingProcessException("coding_process_start_failed");
        try
        {
            var output = ReadAndStopAsync(process.StandardOutput, maximumOutputBytes, onLine, stopping);
            // Error text is consumed within the same budget discipline but never logged or returned.
            var errors = ReadAndStopAsync(process.StandardError, 64 * 1024, null, stopping);
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), stopping.Token);
            process.StandardInput.Close();
            await Task.WhenAll(process.WaitForExitAsync(stopping.Token), output, errors);
            return new(process.ExitCode, await output);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } }
    }

    private static async Task<string> ReadAndStopAsync(StreamReader reader, int maximumBytes,
        Func<string, CancellationToken, Task>? onLine, CancellationTokenSource stopping)
    {
        try { return await ReadAsync(reader, maximumBytes, onLine, stopping.Token); }
        catch { await stopping.CancelAsync(); throw; }
    }

    private static async Task<string> ReadAsync(StreamReader reader, int maximumBytes, Func<string, CancellationToken, Task>? onLine, CancellationToken token)
    {
        var result = new StringBuilder(); var line = new StringBuilder(); var buffer = new char[2048]; var count = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), token);
            if (read == 0) break;
            count += Encoding.UTF8.GetByteCount(buffer.AsSpan(0, read));
            if (count > maximumBytes) throw new CodingProcessException("coding_process_output_limit");
            result.Append(buffer, 0, read);
            if (onLine is null) continue;
            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];
                if (character == '\n') { await onLine(line.ToString().TrimEnd('\r'), token); line.Clear(); }
                else { line.Append(character); if (line.Length > 192 * 1024) throw new CodingProcessException("coding_process_line_limit"); }
            }
        }
        if (line.Length > 0 && onLine is not null) await onLine(line.ToString(), token);
        return result.ToString();
    }
}
internal sealed class CodingProcessException(string code) : Exception(code)
{
    internal string Code { get; } = code;
}
