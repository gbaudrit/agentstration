using Agentstration.Aep.AspNetCore;
using Agentstration.Extensions.Git;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOptions<GitSourceProviderOptions>()
    .Bind(builder.Configuration.GetSection("GitSourceProvider"))
    .Validate(options => !string.IsNullOrWhiteSpace(options.GitExecutable), "GitSourceProvider:GitExecutable is required.")
    .Validate(options => options.MaximumRepositoryBytes > 0, "GitSourceProvider:MaximumRepositoryBytes must be positive.")
    .Validate(options => options.ResolveTimeoutSeconds > 0, "GitSourceProvider:ResolveTimeoutSeconds must be positive.")
    .ValidateOnStart();
builder.Services.AddAgentstrationAep(options =>
{
    options.Extension = new(
        "Agentstration.Extensions.Git",
        "Git Source Provider",
        "1.0.0",
        "Bounded public Git distribution for Agentstration Source Channels.");
    options.OptionSets.Add(GitSourceOptionContracts.SourceChannel);
}).AddSourceProvider<GitSourceProvider>();

var app = builder.Build();
app.MapAgentstrationAep();
await app.RunAsync();

public partial class Program;
