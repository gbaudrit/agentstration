using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Extensions.Utilities;
using ModelContextProtocol.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAgentstrationAep(options =>
{
    options.Extension = new AepExtensionIdentity("Agentstration.Extensions.Utilities", "Deterministic utilities", "1.0.0", "Small sample AEP Tool Provider.");
    options.McpServers.Add(new AepMcpServerDescriptor("utilities", "/mcp"));
    options.Tools.Add(new AepToolContribution("hash.compute", "Compute SHA-256", new("utilities", "hash_compute"), "Compute a SHA-256 digest."));
    options.Tools.Add(new AepToolContribution("json.compact", "Compact JSON", new("utilities", "json_compact"), "Normalize JSON to a compact representation."));
    options.Tools.Add(new AepToolContribution("text.upper", "Uppercase text", new("utilities", "text_upper"), "Convert text to invariant uppercase."));
});
builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly();
var app = builder.Build();
app.MapAgentstrationAep();
app.MapMcp("/mcp");
await app.RunAsync();
public partial class Program;
