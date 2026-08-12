using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAep(options => options.Extension = new AepExtensionIdentity(
    "sample.hello",
    "Hello AEP Extension",
    "1.0.0",
    "Minimal provider-neutral AEP extension."));
var app = builder.Build();
app.MapAep();
await app.RunAsync();

public partial class Program;
