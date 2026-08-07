var builder = DistributedApplication.CreateBuilder(args);
var canonicalFlowPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "Agentstration.Web", ".agentstration", "flow-plane.db"));
var consoleInternalFlowPath = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, ".agentstration", "console-flow-plane.db"));
//var localChatModelName = builder.Configuration["Agentstration:LocalModels:Chat"] ?? "qwen3:1.7b";
//var ollama = builder.AddOllama("ollama").WithDataVolume();
//var localChatModel = ollama.AddModel("local-chat", localChatModelName);

var workApi = builder.AddProject<Projects.Agentstration_Work_Api>("agentstration-work-api")
    .WithEnvironment("AI__Provider", "Deterministic")
    .WithEnvironment("Data__FlowPath", canonicalFlowPath)
    .WithHttpHealthCheck("/health");

var workplace = builder.AddProject<Projects.Agentstration_Workplace_Web>("agentstration-workplace")
    .WithEnvironment("Agentstration__ApiBaseUrl", workApi.GetEndpoint("http"))
    .WithHttpHealthCheck("/health")
    .WaitFor(workApi);

var console = builder.AddProject<Projects.Agentstration_Web>("agentstration-console")
    .WithEnvironment("Agentstration__WorkApi__BaseAddress", workApi.GetEndpoint("http"))
    .WithEnvironment("Data__FlowPath", consoleInternalFlowPath)
    .WithEnvironment("Agentstration__WorkplaceBaseUrl", workplace.GetEndpoint("http"))
    .WithHttpHealthCheck("/health")
    .WaitFor(workApi);
console
    .WithEnvironment("Agentstration__ManagementApi__BaseAddress", console.GetEndpoint("http"))
    .WithEnvironment("Agentstration__RuntimeApi__BaseAddress", console.GetEndpoint("http"))
    .WithEnvironment("Agentstration__FlowApi__BaseAddress", workApi.GetEndpoint("http"));
await builder.Build().RunAsync();
