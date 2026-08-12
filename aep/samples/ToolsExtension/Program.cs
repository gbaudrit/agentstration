using System.ComponentModel;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;

namespace Aep.Samples.Tools;

public sealed class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddAep(options =>
        {
            options.Extension = new("sample.tools", "Generic tools sample", "1.0.0", "Provider-neutral AEP-to-MCP tools sample.");
            options.McpServers.Add(new("tools", "/mcp"));
            options.Tools.Add(new("text.repeat", "Repeat text", new("tools", "text_repeat"), "Repeat text a bounded number of times."));
        });
        builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly();
        var app = builder.Build();
        app.MapAep();
        app.MapMcp("/mcp");
        await app.RunAsync();
    }
}

[McpServerToolType]
public static class SampleTools
{
    [McpServerTool(Name = "text_repeat"), Description("Repeat text between one and five times.")]
    public static string Repeat(string text, [Description("Number of repetitions, from one to five.")] int count = 1) =>
        string.Join(" ", Enumerable.Repeat(text, Math.Clamp(count, 1, 5)));
}
