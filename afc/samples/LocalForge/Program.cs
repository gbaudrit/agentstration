using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Afc.Abstractions;
using Agentstration.Afc.Client;
using Agentstration.Afc.Server;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;

namespace Afc.Samples.LocalForge;

public sealed class SampleAccess
{
    private readonly string generation = Guid.NewGuid().ToString("N");
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public string OtherToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public bool Enabled { get; set; } = true;
    public bool CanWrite { get; set; } = true;
    public string AuthorizationRevision { get; set; } = "grant-1";
    public AfcTargetRef Repository => new("sample-forge", "repository", "fixture", generation);
    public AfcTargetRef Other => new("sample-forge", "other", "fixture", generation);
    public AfcTargetRef ReadOnly => new("sample-forge", "readonly", "fixture", generation);

    public IEnumerable<AfcBinding> Bindings(IAfcProvider provider)
    {
        foreach (var target in new[] { Repository, Other, ReadOnly })
            yield return new(target, provider, (request, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                AfcValidation.Require(Enabled && request.User?.Identity?.IsAuthenticated == true, "forbidden");
                var subject = request.User!.FindFirstValue(ClaimTypes.NameIdentifier)!;
                AfcValidation.Require(subject != "other-subject" || target == Other, "forbidden");
                IReadOnlyList<string> operations = target == ReadOnly || !CanWrite ? ["resolve", "read", "observe"] : AfcContract.Operations;
                return ValueTask.FromResult(new AfcAccessContext(target, subject, AuthorizationRevision, operations));
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
            foreach (var operation in AfcContract.Tools)
            {
                await File.WriteAllTextAsync(Path.Combine(args[1], operation + ".input.json"), AfcSchemas.Input(operation).GetRawText() + "\n");
                await File.WriteAllTextAsync(Path.Combine(args[1], operation + ".output.json"), AfcSchemas.Output(operation).GetRawText() + "\n");
            }
            return;
        }
        var demo = args.Contains("--demo", StringComparer.Ordinal);
        var builder = WebApplication.CreateBuilder(args.Where(a => a != "--demo").ToArray());
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = AfcContract.MaximumRequestBytes);
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<SampleAccess>();
        builder.Services.AddSingleton<IAfcProvider, MemoryForgeProvider>();
        builder.Services.AddSingleton(services => new AfcMcpServer(services.GetRequiredService<SampleAccess>()
            .Bindings(services.GetRequiredService<IAfcProvider>()), clock: services.GetRequiredService<TimeProvider>()));
        builder.Services.AddMcpServer().WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
            .WithListToolsHandler((request, ct) => request.Services!.GetRequiredService<AfcMcpServer>().ListToolsAsync(request, ct))
            .WithCallToolHandler((request, ct) => request.Services!.GetRequiredService<AfcMcpServer>().CallToolAsync(request, ct));
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
            var client = await AfcClient.ConnectAsync(mcp, access.Repository);
            var repository = await client.ResolveAsync(new("demo-resolve", ["feature", "main"]));
            var intent = new AfcCreateIntent(repository.NativeId, "Independent forge fixture",
                repository.Refs.Single(r => r.Name == "feature"), repository.Refs.Single(r => r.Name == "main"),
                Tracking: [new("independent-tracker", "team", "issue-42")]);
            var request = new AfcCreateRequest("demo-create", "demo-operation", AfcContract.Digest(access.Repository, intent), intent);
            var created = await client.CreateAsync(request);
            var recovered = await client.CreateAsync(request with { RequestId = "demo-recover", Mode = AfcMutationMode.Recover });
            AfcValidation.Require(created.ChangeRequest?.Identity == recovered.ChangeRequest?.Identity
                && recovered.Status == AfcReceiptStatus.Applied, "demo_failed");
            var item = created.ChangeRequest!;
            var read = await client.ReadAsync(new("demo-read", repository.NativeId, item.Identity.NativeId));
            var observations = await client.ObserveAsync(new("demo-observe", repository.NativeId, item.Identity.NativeId,
                intent.Head.ObjectId, intent.Base.ObjectId));
            AfcValidation.Require(read.Identity == item.Identity && observations.Status == AfcObservationStatus.Fresh, "demo_failed");
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                contract = AfcContract.Id,
                recovered = true,
                changeRequest = item.Identity.NativeId,
                linkedTracker = item.Tracking.Single().ProviderId
            }));
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
    }
}
