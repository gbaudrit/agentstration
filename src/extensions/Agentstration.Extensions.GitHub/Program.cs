using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Extensions.GitHub;
using ModelContextProtocol.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOptions<GitHubProviderOptions>().Bind(builder.Configuration.GetSection("GitHub"))
    .Validate(value => !string.IsNullOrWhiteSpace(value.StateDirectory) && value.AllowedApiOrigins.Length is >= 1 and <= 16,
        "GitHub requires a state directory and a bounded API origin allowlist.").ValidateOnStart();
builder.Services.AddHttpClient<GitHubConnection>(client => client.Timeout = TimeSpan.FromSeconds(25))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<GitHubOperationJournal>();
builder.Services.AddTransient<GitHubIssueOperations>();
builder.Services.AddTransient<GitHubCodingOperations>();
builder.Services.AddAgentstrationAep(options =>
{
    options.Extension = new("Agentstration.Extensions.GitHub", "GitHub Coding provider", "1.0.0", "Optional native GitHub repository, issue and PR operations.");
    options.McpServers.Add(new("github", "/mcp"));
    options.Tools.Add(new("github.issue", "GitHub issue operations", new("github", "github_issue_operation"),
        Metadata: new Dictionary<string, JsonElement>
        {
            ["agentstration.work.task-provider"] = JsonSerializer.SerializeToElement(new { version = "1.0", operations = new[] { "Read", "Update", "Publish" }, idempotentOperations = true, operationRecovery = true })
        }));
    options.Tools.Add(new("github.coding", "GitHub repository and PR operations", new("github", "github_coding_operation"),
        Metadata: new Dictionary<string, JsonElement>
        {
            ["agentstration.coding.repository"] = JsonSerializer.SerializeToElement(new { version = "1.0", provider = "github", operations = new[] { "repository.resolve", "pull-request.create", "pull-request.read" } })
        }));
});
builder.Services.AddAepEnrollmentAuthentication(builder.Configuration);
builder.Services.AddTransient<GitHubTools>();
builder.Services.AddMcpServer().WithHttpTransport();
foreach (var name in new[] { nameof(GitHubTools.IssueAsync), nameof(GitHubTools.CodingAsync) })
{
    var method = typeof(GitHubTools).GetMethod(name)!;
    builder.Services.AddSingleton<McpServerTool>(services => McpServerTool.Create(method,
        context => context.Services!.GetRequiredService<GitHubTools>(), new()
        {
            Services = services,
            // The existing relay schema profile uses equivalent object schemas for free JSON.
            SchemaCreateOptions = new() { TransformOptions = new() { ConvertBooleanSchemas = true } }
        }));
}
var app = builder.Build();
app.MapAgentstrationAep();
app.MapMcp("/mcp");
await app.RunAsync();
public partial class Program;
