using System.Reflection;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Extensions.Foundry;

var builder = WebApplication.CreateBuilder(args);
var extensionVersion = typeof(FoundryAepModelProvider).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
    ?? throw new InvalidOperationException("The Foundry extension version is missing.");
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
        extensionVersion,
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
