using System.Net;
using System.Net.Sockets;
using Agentstration.Asp.Abstractions;
using Agentstration.Extensions.PostgreSql;
using Npgsql;

namespace Agentstration.Extensions.PostgreSql.Tests;

[TestClass]
public sealed class DocumentConnectionFailureTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ClosedDatabasePortRetainsConnectionRefusalWithoutConnectionValues(bool initialize)
    {
        using var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0)); // Reserved but not listening: deterministic refusal, no database required.
        var connection = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = ((IPEndPoint)reserved.LocalEndPoint!).Port,
            Database = "PRIVATE_DATABASE", Username = "PRIVATE_USER", Password = "PRIVATE_PASSWORD",
            SslMode = SslMode.Disable, Timeout = 1, Pooling = false
        };
        var provider = new PostgreSqlDocumentProvider(connection.ConnectionString, DocumentConformanceTests.Schema);
        var context = new AspDocumentContext(new("store", "binding", new string('a', 64)), new("collection"),
            "caller", "grant", new("collection", QueryProfiles: []));
        var error = await Assert.ThrowsAsync<AspException>(async () =>
        {
            if (initialize) await provider.InitializeAsync(default);
            else _ = await provider.GetAsync(context, new("get", "document"), default);
        });
        Assert.AreEqual("backend_connection_refused", error.Code);
        Assert.DoesNotContain("PRIVATE", error.ToString());
        Assert.DoesNotContain("127.0.0.1", error.ToString());
    }
}
