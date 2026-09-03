using Agentstration.Infrastructure;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class StorageOptionsTests
{
    [TestMethod]
    public void SqliteIsTheDefaultProvider() =>
        Assert.AreEqual(AgentstrationStorageProvider.Sqlite, new AgentstrationStorageOptions().GetProvider());

    [TestMethod]
    public void ProviderNamesAreCaseInsensitive() =>
        Assert.AreEqual(AgentstrationStorageProvider.PostgreSql, new AgentstrationStorageOptions
        {
            Provider = "postgresql",
            ConnectionString = "Host=localhost;Database=agentstration;Username=test;Password=test"
        }.GetProvider());

    [TestMethod]
    public void UnknownProviderIsRejected()
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            new AgentstrationStorageOptions { Provider = "unknown" }.GetProvider());
        StringAssert.Contains(error.Message, "Sqlite");
        StringAssert.Contains(error.Message, "PostgreSql");
    }

    [TestMethod]
    public void PostgreSqlRequiresTheMainConnectionString()
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            new AgentstrationStorageOptions { Provider = "PostgreSql" }.GetProvider());
        StringAssert.Contains(error.Message, "ConnectionStrings:Agentstration");
    }

    [TestMethod]
    public void InvalidPostgreSqlConnectionIsRejectedWithoutEchoingIt()
    {
        const string invalid = "Password=do-not-echo";
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => new AgentstrationStorageOptions
        {
            Provider = "PostgreSql",
            ConnectionString = invalid
        }.GetProvider());
        StringAssert.Contains(error.Message, "not a valid PostgreSQL connection string");
        Assert.IsFalse(error.Message.Contains("do-not-echo", StringComparison.Ordinal));
    }
}
