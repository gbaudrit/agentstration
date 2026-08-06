var builder = DistributedApplication.CreateBuilder(args);
//var localChatModelName = builder.Configuration["Agentstration:LocalModels:Chat"] ?? "qwen3:1.7b";
//var ollama = builder.AddOllama("ollama").WithDataVolume();
//var localChatModel = ollama.AddModel("local-chat", localChatModelName);

builder.AddProject<Projects.Agentstration_Web>("agentstration-console")
    //.WithReference(localChatModel)
    //.WaitFor(localChatModel)
    //.WithEnvironment("AI__Provider", "Ollama")
    //.WithEnvironment("AI__Model", localChatModelName)
    //.WithEnvironment("Agentstration__Seed__OllamaModel", localChatModelName)
    .WithHttpHealthCheck("/health");

var workApi = builder.AddProject<Projects.Agentstration_Work_Api>("agentstration-work-api")
    .WithEnvironment("AI__Provider", "Deterministic")
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.Agentstration_Workplace_Web>("agentstration-workplace")
    .WithEnvironment("Agentstration__ApiBaseUrl", workApi.GetEndpoint("http"))
    .WithHttpHealthCheck("/health")
    .WaitFor(workApi);
await builder.Build().RunAsync();
