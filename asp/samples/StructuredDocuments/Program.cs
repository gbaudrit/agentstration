using System.Security.Cryptography;
using System.Text.Json;
using Agentstration.Asp.Abstractions;
using Agentstration.Asp.OData;
using Agentstration.Asp.Server;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;

namespace Asp.Samples.StructuredDocuments;

public sealed class StructuredSampleAccess
{
    public string Token { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public string Revision { get; set; } = "initial";
    public bool Enabled { get; set; } = true;
    public static AspStoreRef Store { get; } = new("sample", "documents", "1");
    public static JsonElement Schema => JsonSerializer.SerializeToElement(new { type = "object" });
    public AspDocumentPolicy Policy { get; set; } = new("sample", Put: true, Delete: true, Publish: true, QueryProfiles: [ODataDocumentProfile.Id]);
}

public static class StructuredProgram
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 100000);
        builder.Services.AddSingleton(TimeProvider.System); builder.Services.AddSingleton<StructuredSampleAccess>();
        builder.Services.AddSingleton<IAspDocumentProvider>(sp => new MemoryDocumentProvider(StructuredSampleAccess.Schema, sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton(sp => new AspDocumentServer((call, store, selection, ct) =>
        {
            ct.ThrowIfCancellationRequested(); var access = sp.GetRequiredService<StructuredSampleAccess>();
            AspValidation.Require(store == StructuredSampleAccess.Store && selection.Collection == access.Policy.Collection,
                "forbidden", "The sample target is not authorized.");
            return ValueTask.FromResult(new AspDocumentBinding(store, access.Policy, sp.GetRequiredService<IAspDocumentProvider>(), (request, token) =>
            {
                token.ThrowIfCancellationRequested(); AspValidation.Require(access.Enabled, "forbidden", "Access was revoked.");
                return ValueTask.FromResult(new AspDocumentContext(store, selection, "sample-client", access.Revision, access.Policy));
            }));
        }, sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddMcpServer().WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
            .WithListToolsHandler((r, ct) => r.Services!.GetRequiredService<AspDocumentServer>().ListToolsAsync(r, ct))
            .WithCallToolHandler((r, ct) => r.Services!.GetRequiredService<AspDocumentServer>().CallToolAsync(r, ct));
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.Authorization.ToString() != "Bearer " + app.Services.GetRequiredService<StructuredSampleAccess>().Token)
            { context.Response.StatusCode = 401; return; }
            await next(context);
        });
        app.MapMcp("/mcp"); await app.RunAsync();
    }
}
