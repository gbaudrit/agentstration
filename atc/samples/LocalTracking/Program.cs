using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Atc.Abstractions;
using Agentstration.Atc.Client;
using Agentstration.Atc.Server;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;

namespace Atc.Samples.LocalTracking;

public sealed class SampleAccess
{
    private readonly string generation = Guid.NewGuid().ToString("N");
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public string OtherToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public bool Enabled { get; set; } = true;
    public bool CanWrite { get; set; } = true;
    public string AuthorizationRevision { get; set; } = "grant-1";
    public AtcTargetRef Repository => new("sample-authority", "repository", "fixture", generation);
    public AtcTargetRef Team => new("sample-authority", "team", "fixture", generation);
    public AtcTargetRef ReadOnly => new("sample-authority", "readonly", "fixture", generation);

    public IEnumerable<AtcBinding> Bindings(IAtcProvider provider)
    {
        foreach (var target in new[] { Repository, Team, ReadOnly })
            yield return new(target, provider, (request, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                AtcValidation.Require(Enabled && request.User?.Identity?.IsAuthenticated == true, "forbidden");
                var subject = request.User!.FindFirstValue(ClaimTypes.NameIdentifier)!;
                AtcValidation.Require(subject != "other-subject" || target == Team, "forbidden");
                IReadOnlyList<string> operations = target == ReadOnly || !CanWrite ? ["read", "list"] : AtcContract.Operations;
                return ValueTask.FromResult(new AtcAccessContext(target, subject, AuthorizationRevision, operations, []));
            });
    }
}

public sealed class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--schemas")
        {
            Directory.CreateDirectory(args[1]);
            foreach (var operation in AtcContract.Tools)
            {
                await File.WriteAllTextAsync(Path.Combine(args[1], operation + ".input.json"), AtcSchemas.Input(operation).GetRawText() + "\n");
                await File.WriteAllTextAsync(Path.Combine(args[1], operation + ".output.json"), AtcSchemas.Output(operation).GetRawText() + "\n");
            }
            return;
        }
        var demo = args.Contains("--demo", StringComparer.Ordinal);
        var builder = WebApplication.CreateBuilder(args.Where(a => a != "--demo").ToArray());
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = AtcContract.MaximumRequestBytes);
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<SampleAccess>();
        builder.Services.AddSingleton<IAtcProvider, MemoryTrackingProvider>();
        builder.Services.AddSingleton(services => new AtcMcpServer(services.GetRequiredService<SampleAccess>()
            .Bindings(services.GetRequiredService<IAtcProvider>()), clock: services.GetRequiredService<TimeProvider>()));
        builder.Services.AddMcpServer().WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
            .WithListToolsHandler((request, ct) => request.Services!.GetRequiredService<AtcMcpServer>().ListToolsAsync(request, ct))
            .WithCallToolHandler((request, ct) => request.Services!.GetRequiredService<AtcMcpServer>().CallToolAsync(request, ct));
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var access = context.RequestServices.GetRequiredService<SampleAccess>();
            var bearer = context.Request.Headers.Authorization.ToString();
            var subject = bearer == $"Bearer {access.Token}" ? "sample-subject" : bearer == $"Bearer {access.OtherToken}" ? "other-subject" : null;
            if (subject is null) { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject)], "ephemeral-bearer"));
            await next(context);
        });
        app.MapMcp("/mcp");
        if (!demo) { await app.RunAsync(); return; }
        await app.StartAsync();
        try
        {
            var access = app.Services.GetRequiredService<SampleAccess>();
            using var http = new HttpClient(); http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
            await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new() { Endpoint = new(app.Urls.Single() + "/mcp") }, http));
            var client = await AtcClient.ConnectAsync(mcp, access.Team);
            AtcChange change = new AtcCreate("Non-repository tracking fixture", "Created without an agent or forge.");
            var request = new AtcMutationRequest("demo-create", "demo-operation", AtcContract.Digest(access.Team, change), change);
            var created = await client.MutateAsync(request);
            var recovered = await client.MutateAsync(request with { RequestId = "demo-recover", Mode = AtcMutationMode.Recover });
            AtcValidation.Require(created.Item?.Identity == recovered.Item?.Identity
                && recovered.Status == AtcMutationStatus.Applied, "demo_failed");
            var page = await client.ListAsync(new("demo-list"));
            Console.WriteLine(JsonSerializer.Serialize(new { contract = AtcContract.Id, items = page.Items.Count, recovered = true }));
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
    }
}
