using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agentstration.Extensions.PostgreSql.Tests;

[TestClass]
public sealed class TlsPolicyTests
{
    [TestMethod]
    public void TlsRemainsRequiredByDefaultForLocalAndRemoteHosts()
    {
        var options = new PostgreSqlOptions();
        foreach (var host in new[] { "localhost", "127.0.0.1", "::1", "postgres-documents", "database.example.com" })
        {
            Assert.IsFalse(options.AllowsUnencryptedConnection(host));
        }
    }

    [TestMethod]
    public void LegacyLoopbackFlagDoesNotAuthorizeContainerOrRemoteHosts()
    {
        var options = new PostgreSqlOptions { AllowLoopbackWithoutTls = true };
        foreach (var host in new[] { "localhost", "127.0.0.1", "::1" })
        {
            Assert.IsTrue(options.AllowsUnencryptedConnection(host));
        }
        Assert.IsFalse(options.AllowsUnencryptedConnection("postgres-documents"));
        Assert.IsFalse(options.AllowsUnencryptedConnection("database.example.com"));
    }

    [TestMethod]
    public void ExplicitHostOptInMatchesOnlyThatExactHostIgnoringDnsCase()
    {
        var options = new PostgreSqlOptions { AllowedHostsWithoutTls = ["postgres-documents"] };
        Assert.IsTrue(options.AllowsUnencryptedConnection("POSTGRES-DOCUMENTS"));
        foreach (var host in new[] { "localhost", "postgres-documents.example.com", "postgres-documents:5432", "other" })
        {
            Assert.IsFalse(options.AllowsUnencryptedConnection(host));
        }
    }

    [TestMethod]
    public void InvalidHostPatternsAreRejectedByTheRegisteredOptionsBoundary()
    {
        foreach (var host in new[] { "*", "*.example.com", "postgres:5432", "https://database.example.com", "../outside" })
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PostgreSqlDocuments:AllowedClients:0"] = "public-disposable-test-client",
                ["PostgreSqlDocuments:AllowedHostsWithoutTls:0"] = host
            }).Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddLogging();
            services.AddPostgreSqlDocuments(configuration);
            using var provider = services.BuildServiceProvider();
            Assert.Throws<OptionsValidationException>(() => { _ = provider.GetRequiredService<IOptions<PostgreSqlOptions>>().Value; });
        }
    }
}
