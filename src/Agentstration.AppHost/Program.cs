var builder = DistributedApplication.CreateBuilder(args);
//var localChatModelName = builder.Configuration["Agentstration:LocalModels:Chat"] ?? "qwen3:1.7b";
//var ollama = builder.AddOllama("ollama").WithDataVolume();
//var localChatModel = ollama.AddModel("local-chat", localChatModelName);

builder.AddProject<Projects.Agentstration_Web>("agentstration-web")
    //.WithReference(localChatModel)
    //.WaitFor(localChatModel)
    //.WithEnvironment("AI__Provider", "Ollama")
    //.WithEnvironment("AI__Model", localChatModelName)
    //.WithEnvironment("Agentstration__Seed__OllamaModel", localChatModelName)
    .WithHttpHealthCheck("/health");
await builder.Build().RunAsync();
