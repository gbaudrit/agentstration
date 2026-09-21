using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Extensions.Foundry;

var builder = WebApplication.CreateBuilder(args);
var foundry = FoundryExtensionOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(foundry);
builder.Services.AddSingleton(new FoundryBoundConnectionResolver(new HttpClient(new SocketsHttpHandler
{
    AllowAutoRedirect = false
})));
builder.Services.AddHttpClient<FoundryAepModelProvider>(client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => FoundrySecureTransport.Create(foundry));
builder.Services.AddAgentstrationAep(options =>
{
    options.Extension = new(
        "Agentstration.Extensions.Foundry",
        "Microsoft Foundry",
        "1.0.0",
        "Optional Microsoft Foundry model-deployment discovery for Agentstration.");
    options.Capabilities[AepCapabilityNames.SecretAccess] = new(AepProtocol.SecretAccessVersion);
    foreach (var requirement in FoundryValueRequirements.All) options.ValueRequirements.Add(requirement);
});
builder.Services.AddSingleton<IAepModelProvider>(services => services.GetRequiredService<FoundryAepModelProvider>());
builder.Services.AddAepEnrollmentAuthentication(builder.Configuration);

var app = builder.Build();
app.MapAgentstrationAep();
await app.RunAsync();

public partial class Program;
