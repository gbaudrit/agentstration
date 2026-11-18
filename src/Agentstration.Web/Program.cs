using Agentstration.Http.Diagnostics;
using Agentstration.Web.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.AddAgentstrationHttpDiagnostics();
var composition = builder.AddAgentstrationStandaloneHost();
var app = builder.Build();
app.UseAgentstrationHttpDiagnostics();

app.ConfigureAgentstrationStandaloneHost(composition);
await app.InitializeAgentstrationStandaloneHostAsync(composition);
if (app.Lifetime.ApplicationStarted.IsCancellationRequested)
    await app.WaitForShutdownAsync();
else
    await app.RunAsync();

public partial class Program;
