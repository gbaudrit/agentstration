using Agentstration.Aep.AspNetCore;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Agentstration.Extensions.Foundry;

var builder = WebApplication.CreateBuilder(args);
var foundry = FoundryExtensionOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(foundry);
builder.Services.AddSingleton(services => new FoundryRequestAuthenticator(
    foundry,
    builder.Environment.IsDevelopment() ? builder.Configuration["FOUNDRY_API_KEY"] : null,
    httpClientFactory: services.GetRequiredService<IHttpClientFactory>()));
builder.Services.AddHttpClient("foundry-secret-access")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
builder.Services.AddHttpClient<FoundryAepModelProvider>(client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => FoundrySecureTransport.Create(foundry));
builder.Services.AddAgentstrationAep(options =>
{
    options.Extension = new(
        "Agentstration.Extensions.Foundry",
        "Microsoft Foundry",
        "1.0.0",
        "Optional Microsoft Foundry model-deployment discovery for Agentstration.");
    options.SecretRequirements.Add(new AepSecretRequirement("credential", Required: false,
        "Foundry API key for a bound Model Provider or Model Profile."));
    options.Capabilities[AepCapabilityNames.SecretAccess] = new(AepProtocol.SecretAccessVersion);
});
builder.Services.AddSingleton<IAepModelProvider>(services => services.GetRequiredService<FoundryAepModelProvider>());
builder.Services.AddAepEnrollmentAuthentication(builder.Configuration);

var app = builder.Build();
app.MapAgentstrationAep();
await app.RunAsync();

public partial class Program;
