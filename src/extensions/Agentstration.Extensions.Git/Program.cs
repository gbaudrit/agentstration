using Agentstration.Aep.AspNetCore;
using Agentstration.Extensions.Git;
using System.Text.Json;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 2 * 1024 * 1024);
builder.Services.AddOptions<GitSourceProviderOptions>()
    .Bind(builder.Configuration.GetSection("GitSourceProvider"))
    .Validate(options => !string.IsNullOrWhiteSpace(options.GitExecutable), "GitSourceProvider:GitExecutable is required.")
    .Validate(options => options.MaximumRepositoryBytes > 0, "GitSourceProvider:MaximumRepositoryBytes must be positive.")
    .Validate(options => options.ResolveTimeoutSeconds > 0, "GitSourceProvider:ResolveTimeoutSeconds must be positive.")
    .ValidateOnStart();
builder.Services.AddOptions<GitOperationsOptions>().Bind(builder.Configuration.GetSection("GitOperations"))
    .Validate<IOptions<GitSourceProviderOptions>>((options, source) => GitRepositoryConnection.ValidOptions(options, source.Value),
        "Enabled Git operations require bounded repositories, a state directory and a valid timeout.").ValidateOnStart();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<GitPublicationJournal>();
builder.Services.AddSingleton<GitRepositoryOperations>();
builder.Services.AddAgentstrationAep(options =>
{
    options.Extension = new(
        "Agentstration.Extensions.Git",
        "Git Source Provider",
        "1.0.0",
        "Bounded public Git distribution for Agentstration Source Channels.");
    options.OptionSets.Add(GitSourceOptionContracts.SourceChannel);
    options.ConnectedServiceTypes.Add(new(GitServiceContract.TypeId, "Git", GitServiceContract.Version, [GitServiceContract.TypeVersion]));
    options.McpServers.Add(new("git", "/mcp"));
    options.Tools.Add(new("git.repository", "Generic Git operations", new("git", "git_repository_operation"),
        Metadata: new Dictionary<string, JsonElement>
        {
            [GitServiceContract.Metadata] = GitServiceContract.TransportDeclaration,
            [GitOperationProtocol.MetadataKey] = JsonSerializer.SerializeToElement(new
            { version = GitOperationProtocol.Version, operations = new[] { "resolve", "read", "diff", "publish" }, expectedBase = true, exactRevisionReads = true, historyRead = true, idempotentOperations = true, operationRecovery = true })
        }));
}).AddSourceProvider<GitSourceProvider>();
builder.Services.AddAepEnrollmentAuthentication(builder.Configuration);
builder.Services.AddMcpServer().WithHttpTransport().WithTools<GitRepositoryTools>();

var app = builder.Build();
app.MapAgentstrationAep();
app.MapMcp("/mcp");
await app.RunAsync();

public partial class Program;
