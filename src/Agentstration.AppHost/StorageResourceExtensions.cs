using Aspire.Hosting.ApplicationModel;

namespace Agentstration.AppHost;

internal static class StorageResourceExtensions
{
    public static IResourceBuilder<ProjectResource> WithSqliteStorage(
        this IResourceBuilder<ProjectResource> console,
        string dataPath) => console
            .WithEnvironment("Agentstration__Storage__Provider", "Sqlite")
            .WithEnvironment("Data__ControlPlanePath", Path.Combine(dataPath, "control-plane.db"))
            .WithEnvironment("Data__WorkPlanePath", Path.Combine(dataPath, "work-plane.db"))
            .WithEnvironment("Data__FlowPath", Path.Combine(dataPath, "flow-plane.db"))
            .WithEnvironment("Data__RuntimePath", Path.Combine(dataPath, "runtime-plane.db"));

    public static IResourceBuilder<ProjectResource> WithPostgreSqlStorage(
        this IResourceBuilder<ProjectResource> console,
        IDistributedApplicationBuilder builder,
        string slot)
    {
        var password = builder.AddParameter(
            "postgres-password",
            new GenerateParameterDefault(),
            secret: true,
            persist: true);
        var postgres = builder.AddPostgres("postgres", password: password)
            .WithImage("postgres")
            .WithImageTag("17")
            .WithDataVolume($"agentstration-{slot}-postgresql");
        var database = postgres.AddDatabase("agentstration");

        return console
            .WithEnvironment("Agentstration__Storage__Provider", "PostgreSql")
            .WithReference(database)
            .WaitFor(database);
    }
}
