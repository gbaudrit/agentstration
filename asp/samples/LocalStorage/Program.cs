using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Agentstration.Asp.Abstractions;
using Agentstration.Asp.Client;
using Agentstration.Asp.Server;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;

namespace Asp.Samples.LocalStorage;

public sealed class SampleAccess
{
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public string OtherToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public bool Enabled { get; set; } = true;
    public string Revision { get; set; } = "grant-1";
    public static AspStoreRef FirstStore { get; } = new("first", "memory", "binding-1");
    public static AspStoreRef SecondStore { get; } = new("second", "memory", "binding-1");
    public static AspStoreRef ReadOnlyStore { get; } = new("readonly", "memory", "binding-1");
    public static AspAccessPolicy Policy { get; } = new(true, true, true, true, "notes/", 4096, 4096, 10);

    public IEnumerable<AspStoreBinding> Bindings(IAspObjectReader provider)
    {
        yield return Binding(FirstStore, Policy, provider);
        yield return Binding(SecondStore, Policy, provider);
        yield return Binding(ReadOnlyStore, Policy with { CanWrite = false }, new ReadOnlyObjectProvider(provider));
    }

    private AspStoreBinding Binding(AspStoreRef store, AspAccessPolicy policy, IAspObjectReader provider) =>
        new(store, policy, provider, (request, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            AspValidation.Require(Enabled && request.User?.Identity?.IsAuthenticated == true, "forbidden", "Access is not authorized.");
            var subject = request.User!.FindFirstValue(ClaimTypes.NameIdentifier)!;
            AspValidation.Require(subject != "other-subject" || store == FirstStore,
                "forbidden", "The caller is not authorized for the selected logical target.");
            return ValueTask.FromResult(new AspAccessContext(store, subject, Revision, policy));
        });
}

public sealed class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--schemas")
        {
            Directory.CreateDirectory(args[1]);
            foreach (var operation in new[] { "describe", "list", "stat", "read_text", "read_blob", "write_text", "write_blob", "close_read", "abort_write", "write_status" })
            {
                await File.WriteAllTextAsync(Path.Combine(args[1], operation + ".input.json"), AspSchemas.Input(operation).GetRawText() + "\n");
                await File.WriteAllTextAsync(Path.Combine(args[1], operation + ".output.json"), AspSchemas.Output(operation).GetRawText() + "\n");
            }
            return;
        }
        var demo = args.Contains("--demo", StringComparer.Ordinal);
        var builder = WebApplication.CreateBuilder(args.Where(value => value != "--demo").ToArray());
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 100_000);
        builder.Services.AddSingleton<SampleAccess>();
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<IAspObjectReader, MemoryObjectProvider>();
        builder.Services.AddSingleton(services => new AspMcpServer(
            services.GetRequiredService<SampleAccess>().Bindings(services.GetRequiredService<IAspObjectReader>()),
            services.GetRequiredService<TimeProvider>()));
        builder.Services.AddMcpServer().WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithListToolsHandler((request, ct) => request.Services!.GetRequiredService<AspMcpServer>().ListToolsAsync(request, ct))
            .WithCallToolHandler((request, ct) => request.Services!.GetRequiredService<AspMcpServer>().CallToolAsync(request, ct));
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var access = context.RequestServices.GetRequiredService<SampleAccess>();
            var bearer = context.Request.Headers.Authorization.ToString();
            var subject = bearer == $"Bearer {access.Token}" ? "sample-reader-writer"
                : bearer == $"Bearer {access.OtherToken}" ? "other-subject" : null;
            if (subject is null) { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject)], "sample-ephemeral-bearer"));
            await next(context);
        });
        app.MapMcp("/mcp");
        if (!demo) { await app.RunAsync(); return; }
        await app.StartAsync();
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", app.Services.GetRequiredService<SampleAccess>().Token);
            await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new()
            { Endpoint = new Uri(app.Urls.Single() + "/mcp") }, http));
            var client = await AspClient.ConnectAsync(mcp, "asp", SampleAccess.FirstStore);
            var bytes = Encoding.UTF8.GetBytes("{\"Opaque_KEY\":{\"Value\":42}}");
            var written = await client.WriteBlobAsync(new("demo-write", 0, true, Convert.ToBase64String(bytes), Options: new("notes/example.json"), NotAfter: DateTimeOffset.UtcNow.AddMinutes(2)));
            var page = await client.ListAsync(new("demo-list", "notes/", 1));
            var metadata = await client.StatAsync(new("demo-stat", written.Receipt!.Item.Reference));
            // A small complete text call and a complete binary call use the same session API.
            var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
            var text = await client.WriteTextAsync(new("text-write", 0, true, "é🌍\r\n", Options: new("notes/text"), NotAfter: deadline));
            var completeText = await client.ReadTextAsync(new("text-read", Selection: new(text.Receipt!.Item.Reference, PreferredChunkSizeBytes: 100), NotAfter: deadline));
            var completeBlob = await client.ReadBlobAsync(new("blob-read", Selection: new(written.Receipt.Item.Reference, PreferredChunkSizeBytes: 100), NotAfter: deadline));
            // Progressive text preserves exact characters; only the first call supplies options/deadline.
            var firstText = await client.WriteTextAsync(new("text-first", 0, false, "é", Options: new("notes/progressive-text"), NotAfter: deadline));
            var lastText = await client.WriteTextAsync(new("text-last", 1, true, "🌍", SessionRef: firstText.SessionRef));
            var firstRead = await client.ReadTextAsync(new("text-progressive-read", Selection: new(lastText.Receipt!.Item.Reference, PreferredChunkSizeBytes: 1), NotAfter: deadline));
            var collected = new StringBuilder(firstRead.Content);
            for (var current = firstRead; !current.Final;)
            {
                current = await client.ReadTextAsync(new("text-read-" + (current.Sequence + 1), current.Sequence + 1, current.SessionRef));
                collected.Append(current.Content);
            }
            // Stream adapters use progressive binary sessions and bounded buffers.
            await using var output = client.OpenWriteStream(new("notes/progressive-blob"), "stream-write", deadline);
            await output.WriteAsync(bytes);
            var receipt = await output.CompleteAsync();
            await using var input = client.OpenReadStream(new(receipt.Item.Reference), "stream-read", deadline);
            using var content = new MemoryStream();
            await input.CopyToAsync(content);
            var range = await client.ReadBlobAsync(new("range-read", Selection: new(receipt.Item.Reference, new(2, 4), 100), NotAfter: deadline));
            var readonlyClient = await AspClient.ConnectAsync(mcp, "asp", SampleAccess.ReadOnlyStore);
            Console.WriteLine($"ASP {client.Description.AspVersion}: {metadata.Length} bytes, write={client.Description.Capabilities.Write}, readonly.write={readonlyClient.Description.Capabilities.Write}");
            Console.WriteLine(Encoding.UTF8.GetString(content.ToArray()));
            Console.WriteLine($"Complete text/blob: {completeText.ByteLength}/{completeBlob.ByteLength} bytes; progressive text: {collected}; range: {range.Offset}+{range.ByteLength}");
        }
        finally { await app.StopAsync(); await app.DisposeAsync(); }
    }
}
