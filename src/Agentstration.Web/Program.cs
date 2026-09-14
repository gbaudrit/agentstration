using Agentstration.Web.Hosting;

var builder = WebApplication.CreateBuilder(args);
var composition = builder.AddAgentstrationStandaloneHost();
var app = builder.Build();

app.ConfigureAgentstrationStandaloneHost(composition);
await app.InitializeAgentstrationStandaloneHostAsync(composition);
await app.RunAsync();

public partial class Program;
