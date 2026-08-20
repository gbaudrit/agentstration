using Agentstration.Aep.AspNetCore;
using Agentstration.Extensions.Memory.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var configuredPath = builder.Configuration["MemorySqlite:Path"];
var databasePath = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredPath)
    ? Path.Combine(".agentstration", "extensions", "memory-sqlite", "memory.db")
    : configuredPath);
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
var connectionString = new SqliteConnectionStringBuilder
{
    DataSource = databasePath,
    Mode = SqliteOpenMode.ReadWriteCreate,
    Cache = SqliteCacheMode.Shared,
    Pooling = true
}.ToString();

builder.Services.AddDbContextFactory<SqliteAepMemoryDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddSingleton<SqliteAepMemoryProvider>();
builder.Services.AddSingleton<IAepMemoryProvider>(services => services.GetRequiredService<SqliteAepMemoryProvider>());
builder.Services.AddAgentstrationAep(options => options.Extension = new(
    "Agentstration.Extensions.Memory.Sqlite",
    "SQLite Memory",
    "1.0.0",
    "Durable local SQLite AEP Memory store provider."));

var app = builder.Build();
await app.Services.GetRequiredService<SqliteAepMemoryProvider>().InitializeAsync(app.Lifetime.ApplicationStopping);
app.MapAgentstrationAep();
await app.RunAsync();

public partial class Program;
