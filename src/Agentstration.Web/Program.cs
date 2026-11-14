using Agentstration.Http.Diagnostics;
using Agentstration.Web.Hosting;

var builder = WebApplication.CreateBuilder(args);
builder.AddAgentstrationHttpDiagnostics();
var composition = builder.AddAgentstrationStandaloneHost();
var app = builder.Build();
app.UseAgentstrationHttpDiagnostics();

app.ConfigureAgentstrationStandaloneHost(composition);
await app.InitializeAgentstrationStandaloneHostAsync(composition);
await app.RunAsync();

public partial class Program;
