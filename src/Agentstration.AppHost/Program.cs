var builder = DistributedApplication.CreateBuilder(args);
//var localChatModelName = builder.Configuration["Agentstration:LocalModels:Chat"] ?? "qwen3:1.7b";
//var ollama = builder.AddOllama("ollama").WithDataVolume();
//var localChatModel = ollama.AddModel("local-chat", localChatModelName);

var workApi = builder.AddProject<Projects.Agentstration_Work_Api>("agentstration-work-api")
    .WithEnvironment("AI__Provider", "Deterministic")
    .WithHttpHealthCheck("/health");

var workplace = builder.AddProject<Projects.Agentstration_Workplace_Web>("agentstration-workplace")
    .WithEnvironment("Agentstration__ApiBaseUrl", workApi.GetEndpoint("http"))
    .WithHttpHealthCheck("/health")
    .WaitFor(workApi);

var console = builder.AddProject<Projects.Agentstration_Web>("agentstration-console")
    .WithEnvironment("Agentstration__WorkApi__BaseAddress", workApi.GetEndpoint("http"))
    .WithEnvironment("Agentstration__WorkplaceBaseUrl", workplace.GetEndpoint("http"))
    .WithHttpHealthCheck("/health")
    .WaitFor(workApi);
console
    .WithEnvironment("Agentstration__ManagementApi__BaseAddress", console.GetEndpoint("http"))
    .WithEnvironment("Agentstration__RuntimeApi__BaseAddress", console.GetEndpoint("http"))
    .WithEnvironment("Agentstration__FlowApi__BaseAddress", console.GetEndpoint("http"));
await builder.Build().RunAsync();
