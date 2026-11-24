using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Agentstration.Testing;

internal sealed class AuthenticatedGitFixture : IAsyncDisposable
{
    // Public disposable test data; never a live Git credential.
    internal const string Credential = "public-disposable-authenticated-git-fixture";
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "agentstration-git-operations-" + Guid.NewGuid().ToString("N"));
    internal string RepositoryPath => Path.Combine(Root, "repository.git");
    internal string Work => Path.Combine(Root, "work");
    internal string Repository { get; private set; } = "";
    internal string InitialRevision { get; private set; } = "";
    internal bool LoseNextWriteResponse { get; set; }
    internal bool Unavailable { get; set; }
    internal Func<Task>? BeforeWrite { get; set; }
    private WebApplication? server;

    internal static async Task<AuthenticatedGitFixture> CreateAsync()
    {
        var fixture = new AuthenticatedGitFixture(); Directory.CreateDirectory(fixture.Root); Directory.CreateDirectory(fixture.Work);
        await fixture.GitAsync(fixture.Root, "init", "--bare", "--initial-branch=main", fixture.RepositoryPath);
        await fixture.GitAsync(fixture.RepositoryPath, "config", "http.receivepack", "true");
        await fixture.GitAsync(fixture.Work, "init", "--initial-branch=main");
        await fixture.GitAsync(fixture.Work, "config", "user.name", "Agentstration fixture");
        await fixture.GitAsync(fixture.Work, "config", "user.email", "fixture@agentstration.invalid");
        Directory.CreateDirectory(Path.Combine(fixture.Work, "shared"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Work, "shared", "plan.json"), "{\"revision\":1}");
        await File.WriteAllTextAsync(Path.Combine(fixture.Work, "private.txt"), "Not granted by the connection path policy");
        await fixture.GitAsync(fixture.Work, "add", "."); await fixture.GitAsync(fixture.Work, "commit", "-m", "Initial fixture");
        fixture.InitialRevision = (await fixture.GitAsync(fixture.Work, "rev-parse", "HEAD")).Trim();
        await fixture.GitAsync(fixture.Work, "push", fixture.RepositoryPath, "HEAD:refs/heads/main");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024 * 1024);
        fixture.server = builder.Build();
        fixture.server.MapMethods("/{**path}", ["GET", "POST"], fixture.HandleAsync);
        await fixture.server.StartAsync();
        fixture.Repository = fixture.server.Urls.Single().TrimEnd('/') + "/repository.git";
        return fixture;
    }

    private async Task HandleAsync(HttpContext context)
    {
        var authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture:" + Credential));
        if (context.Request.Headers.Authorization != authorization)
        { context.Response.StatusCode = 401; context.Response.Headers.WWWAuthenticate = "Basic realm=fixture"; return; }
        if (Unavailable) { context.Response.StatusCode = 503; return; }
        if (!context.Request.Path.StartsWithSegments("/repository.git")) { context.Response.StatusCode = 404; return; }
        if (context.Request.Path.Value!.EndsWith("/git-receive-pack", StringComparison.Ordinal) && BeforeWrite is { } before)
        { BeforeWrite = null; await before(); }
        var token = context.RequestAborted;
        var start = new ProcessStartInfo("git") { WorkingDirectory = Root, UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("http-backend"); start.Environment["GIT_PROJECT_ROOT"] = Root; start.Environment["GIT_HTTP_EXPORT_ALL"] = "1";
        start.Environment["PATH_INFO"] = context.Request.Path.Value; start.Environment["REQUEST_METHOD"] = context.Request.Method;
        start.Environment["QUERY_STRING"] = context.Request.QueryString.Value?.TrimStart('?') ?? "";
        start.Environment["CONTENT_TYPE"] = context.Request.ContentType ?? "";
        start.Environment["REMOTE_USER"] = "fixture";
        if (context.Request.ContentLength is { } length) start.Environment["CONTENT_LENGTH"] = length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the native fixture backend.");
        try
        {
            var outputTask = ReadAsync(process.StandardOutput.BaseStream, token);
            var errorTask = process.StandardError.ReadToEndAsync(token);
            await context.Request.Body.CopyToAsync(process.StandardInput.BaseStream, token); process.StandardInput.Close();
            await process.WaitForExitAsync(token); var output = await outputTask; await errorTask;
            if (LoseNextWriteResponse && context.Request.Path.Value!.EndsWith("/git-receive-pack", StringComparison.Ordinal))
            { LoseNextWriteResponse = false; Unavailable = true; context.Abort(); return; }
            var separator = output.AsSpan().IndexOf("\r\n\r\n"u8);
            if (separator < 0) throw new InvalidOperationException("Invalid native Git fixture response.");
            foreach (var header in Encoding.ASCII.GetString(output, 0, separator).Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = header.Split(':', 2); if (pair.Length != 2) continue;
                if (pair[0].Equals("Status", StringComparison.OrdinalIgnoreCase)) context.Response.StatusCode = int.Parse(pair[1].Trim().Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
                else context.Response.Headers[pair[0]] = pair[1].Trim();
            }
            await context.Response.Body.WriteAsync(output.AsMemory(separator + 4), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { if (!process.HasExited) process.Kill(true); }
    }
    private static async Task<byte[]> ReadAsync(Stream source, CancellationToken token)
    {
        using var output = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = await source.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + read > 16 * 1024 * 1024) throw new InvalidOperationException("Native fixture response exceeded its bound.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        return output.ToArray();
    }
    internal async Task<string> GitAsync(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start fixture Git.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("Fixture Git command failed: " + await error);
        await error; return await output;
    }
    public async ValueTask DisposeAsync()
    {
        if (server is not null) await server.DisposeAsync();
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
    }
}
