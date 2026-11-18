using System.Security.Claims;
using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.AspNetCore;
using Agentstration.Aep.Client;
using Agentstration.Asp.Abstractions;
using Agentstration.Asp.Aep;
using Agentstration.Asp.Server;
using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using Npgsql;

namespace Agentstration.Extensions.PostgreSql;

public sealed class PostgreSqlOptions
{
    public string[] AllowedClients { get; set; } = [];
    public bool AllowPairedClient { get; set; }
    public bool ProvisionSchema { get; set; }
    public bool AllowLoopbackWithoutTls { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Authorizes(ClaimsPrincipal? caller) => Enabled && caller?.Identity?.IsAuthenticated == true
        && caller.HasClaim(AepAuthenticationDefaults.PermissionClaim, AepAuthenticationDefaults.InvokePermission)
        && (AllowedClients.Contains(caller.FindFirstValue(ClaimTypes.NameIdentifier), StringComparer.Ordinal)
            || AllowPairedClient && caller.HasClaim(AepAuthenticationDefaults.PairedClientClaim, "true"));
}

public sealed class PostgreSqlBindingFactory(IOptionsMonitor<PostgreSqlOptions> options,
    IHttpClientFactory clients, TimeProvider? timeProvider = null) : IAspDocumentAepBindingFactory
{
    public IReadOnlyList<AepStorageTypeContribution> Contributions => PostgreSqlStorageTypes.Contributions;
    public async ValueTask<AspDocumentBinding> CreateAsync(AepStorageBindingRequest request, ClaimsPrincipal caller, CancellationToken ct)
    {
        AspValidation.Require(options.CurrentValue.Authorizes(caller), "forbidden", "The PostgreSQL caller is not authorized.");
        AspValidation.Require(request.Configuration.ValueKind == JsonValueKind.Object && !request.Configuration.EnumerateObject().Any(),
            "invalid_request", "JSONB physical layout is owned by the provider.");
        var store = AspValidation.Parse<AspStoreRef>(request.Store);
        var requested = AspValidation.Parse<AspDocumentPolicy>(request.DatasetRule);
        var declarations = request.DocumentCollections!.Value.Deserialize<AspDocumentCollectionDeclaration[]>(AspProtocol.JsonOptions)!;
        AspValidation.Require(declarations.Length is > 0 and <= 32 && declarations.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() == declarations.Length,
            "invalid_request", "Invalid logical collection declarations.");
        var collection = declarations.SingleOrDefault(c => c.Id == requested.Collection)
            ?? throw new AspException("forbidden", "The requested collection is not configured.");
        DocumentModel.ValidateSchema(collection.Schema);
        var envelope = AspValidation.Parse<PostgreSqlDocumentEnvelope>(request.Envelope).For(collection.Id);
        var policy = AspDocumentValidation.Intersect(AspDocumentValidation.Intersect(envelope, collection.Policy), requested);
        using var http = clients.CreateClient("postgresql-secret-access");
        var values = new AepValueResolver(request.Connection, http);
        async Task<string> Text(string name)
        {
            using var value = await values.ResolveAsync(name, ct); var text = value.ReadString();
            AspValidation.Require(text is { Length: > 0 and <= 4096 } && !text.Any(char.IsControl), "invalid_request", "Invalid connection value.");
            return text;
        }
        var host = await Text("host"); var database = await Text("database"); var username = await Text("username"); var password = await Text("password");
        var portBinding = request.Connection.Single(v => v.RequirementId == "port");
        AspValidation.Require(portBinding.InlineValue is { ValueKind: JsonValueKind.Number } portValue && portValue.TryGetInt32(out var port) && port is > 0 and <= 65535,
            "invalid_request", "A typed PostgreSQL port is required.");
        var local = host is "localhost" or "127.0.0.1" or "::1";
        var connection = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = portBinding.InlineValue!.Value.GetInt32(),
            Database = database,
            Username = username,
            Password = password,
            SslMode = local && options.CurrentValue.AllowLoopbackWithoutTls ? SslMode.Disable : SslMode.VerifyFull,
            MaxPoolSize = 4,
            MinPoolSize = 0,
            Timeout = 10,
            CommandTimeout = 10,
            IncludeErrorDetail = false,
            ApplicationName = "Agentstration ASP JSONB"
        };
        var provider = new PostgreSqlDocumentProvider(connection.ConnectionString, collection.Schema, timeProvider);
        if (options.CurrentValue.ProvisionSchema) await provider.InitializeAsync(ct);
        var subject = caller.FindFirstValue(ClaimTypes.NameIdentifier)!;
        return new(store, policy, provider, (call, token) =>
        {
            token.ThrowIfCancellationRequested();
            AspValidation.Require(options.CurrentValue.Authorizes(call.User) && call.User!.FindFirstValue(ClaimTypes.NameIdentifier) == subject,
                "forbidden", "The caller is no longer authorized.");
            return ValueTask.FromResult(new AspDocumentContext(store, new(collection.Id), subject, "postgresql-grant-v1", policy));
        });
    }
}

public static class PostgreSqlHosting
{
    public static IServiceCollection AddPostgreSqlDocuments(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PostgreSqlOptions>().BindConfiguration("PostgreSqlDocuments")
            .Validate(o => o.AllowedClients.Length <= 32 && o.AllowedClients.All(c => c.Length is > 0 and <= 128)
                && (o.AllowedClients.Length > 0 || o.AllowPairedClient), "An explicit PostgreSQL AEP caller is required.")
            .Validate(o => !o.AllowPairedClient || string.Equals(configuration["Aep:EnrollmentMode"], nameof(AepEnrollmentMode.PairingCode), StringComparison.OrdinalIgnoreCase),
                "Paired PostgreSQL callers require AEP PairingCode enrollment.").ValidateOnStart();
        services.AddSingleton(TimeProvider.System); services.AddHttpContextAccessor();
        services.AddHttpClient("postgresql-secret-access", c => c.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        services.AddSingleton<IAspDocumentAepBindingFactory, PostgreSqlBindingFactory>();
        services.AddAgentstrationAep(o =>
        {
            o.Extension = new(PostgreSqlStorageTypes.ExtensionId, "PostgreSQL structured storage", "1.0.0");
            o.Capabilities[AepCapabilityNames.BoundValues] = new(AepProtocol.BoundValuesCapabilityVersion);
            o.Capabilities[AepCapabilityNames.SecretAccess] = new(AepProtocol.SecretAccessVersion);
            o.McpServers.Add(new("documents", "/mcp"));
            foreach (var type in PostgreSqlStorageTypes.Contributions) o.StorageTypes.Add(type);
            o.ConnectedServiceTypes.Add(PostgreSqlConnectedServiceType.Contribution);
        });
        services.AddAepEnrollmentAuthentication(configuration);
        services.AddSingleton(sp => new AspDocumentAepBindings(sp.GetRequiredService<IAspDocumentAepBindingFactory>(),
            sp.GetRequiredService<IHttpContextAccessor>(), caller => sp.GetRequiredService<IOptionsMonitor<PostgreSqlOptions>>().CurrentValue.Authorizes(caller),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new AspDocumentServer(sp.GetRequiredService<AspDocumentAepBindings>().ResolveAsync, sp.GetRequiredService<TimeProvider>()));
        services.AddMcpServer().WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless)
            .WithListToolsHandler((r, ct) => r.Services!.GetRequiredService<AspDocumentServer>().ListToolsAsync(r, ct))
            .WithCallToolHandler((r, ct) => r.Services!.GetRequiredService<AspDocumentServer>().CallToolAsync(r, ct));
        return services;
    }
    public static WebApplication MapPostgreSqlDocuments(this WebApplication app)
    {
        app.UseAuthentication(); app.UseAuthorization(); app.MapAgentstrationAep(); app.MapAspDocumentAepBindings();
        app.MapMcp("/mcp").RequireAuthorization(AepAuthenticationDefaults.Policy); return app;
    }
}

public static class PostgreSqlProgram
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 100000);
        builder.Services.AddPostgreSqlDocuments(builder.Configuration);
        var app = builder.Build(); app.MapPostgreSqlDocuments(); await app.RunAsync();
    }
}
