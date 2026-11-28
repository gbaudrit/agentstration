using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Afc.Aep;
using Agentstration.Atc.Aep;
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
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<GitHubContractMcpServer>();
builder.Services.AddAgentstrationAep(options =>
{
    options.Extension = new("Agentstration.Extensions.GitHub", "GitHub Coding provider", "1.0.0", "Optional native GitHub repository, issue and PR operations.");
    options.McpServers.Add(new("github", "/mcp"));
    options.ConnectedServiceTypes.Add(new(GitHubServiceContract.TypeId, "GitHub", GitHubServiceContract.Version, [GitHubServiceContract.TypeVersion]));
    foreach (var contribution in AtcAepContributions.Create("github.tracking", "github", "github_atc")
        .Concat(AfcAepContributions.Create("github.forge", "github", "github_afc")))
        options.Tools.Add(contribution with
        {
            Metadata = new Dictionary<string, JsonElement>(contribution.Metadata!)
            { [GitHubServiceContract.Metadata] = GitHubServiceContract.TransportDeclaration }
        });
    options.Tools.Add(new("github.issue", "GitHub issue operations", new("github", "github_issue_operation"),
        Metadata: new Dictionary<string, JsonElement>
        {
            ["agentstration.coding.issue-authority"] = JsonSerializer.SerializeToElement(new { version = "1.0", provider = "github", nativeHierarchy = true, nativeDependencies = true, completionReason = true, managedLabels = true, revisionObservation = true }),
            ["agentstration.work.task-provider"] = JsonSerializer.SerializeToElement(new { version = "1.0", operations = new[] { "Read", "Update", "Publish" }, idempotentOperations = true, operationRecovery = true })
        }));
    options.Tools.Add(new("github.coding", "GitHub repository and PR operations", new("github", "github_coding_operation"),
        Metadata: new Dictionary<string, JsonElement>
        {
            ["agentstration.coding.repository"] = JsonSerializer.SerializeToElement(new { version = "1.0", provider = "github", operations = new[] { "repository.resolve", "pull-request.create", "pull-request.read" } })
        }));
    options.ToolSets.Add(new("github-read", "GitHub read", "1.0.0",
        [
            new("github.tracking.describe", "github.tracking.describe"),
            new("github.tracking.list", "github.tracking.list"),
            new("github.tracking.read", "github.tracking.read"),
            new("github.forge.describe", "github.forge.describe"),
            new("github.forge.resolve", "github.forge.resolve"),
            new("github.forge.read", "github.forge.read"),
            new("github.forge.observe", "github.forge.observe")
        ], "Read-only Tracking and Forge tools. Enrollment does not activate tools or assign consumer access."));
    options.ToolSets.Add(new("github-write", "GitHub write", "1.0.0",
        [
            new("github.tracking.mutate", "github.tracking.mutate"),
            new("github.forge.create", "github.forge.create"),
            new("github.issue", "github.issue"),
            new("github.coding", "github.coding")
        ], "Mutating Tracking and Forge tools, including the mixed issue and coding tools. Enrollment does not activate tools or assign consumer access."));
});
builder.Services.AddAepEnrollmentAuthentication(builder.Configuration);
builder.Services.AddTransient<GitHubTools>();
builder.Services.AddMcpServer().WithHttpTransport(options => options.SessionMode = ModelContextProtocol.AspNetCore.HttpServerSessionMode.Stateless)
    .WithListToolsHandler((request, ct) => request.Services!.GetRequiredService<GitHubContractMcpServer>().ListAsync(request, ct))
    .WithCallToolHandler((request, ct) => request.Services!.GetRequiredService<GitHubContractMcpServer>().CallAsync(request, ct));
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
