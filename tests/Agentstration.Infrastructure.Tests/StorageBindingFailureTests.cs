using System.Net;
using System.Text;
using Agentstration.Asp.Abstractions;
using Agentstration.Infrastructure.Storage;

namespace Agentstration.Infrastructure.Tests;

[TestClass]
public sealed class StorageBindingFailureTests
{
    [TestMethod]
    [DataRow(503, "{\"code\":\"backend_connection_refused\",\"detail\":\"PRIVATE\"}", "backend_connection_refused")]
    [DataRow(504, "{\"code\":\"backend_timeout\"}", "backend_timeout")]
    [DataRow(503, "{\"code\":\"backend_unavailable\"}", "backend_unavailable")]
    [DataRow(500, "PRIVATE INVALID JSON", "provider_unavailable")]
    [DataRow(422, "{\"code\":\"PRIVATE ARBITRARY CODE\"}", "binding_refused")]
    [DataRow(403, "{\"code\":\"backend_connection_refused\"}", "forbidden")]
    public async Task ProtectedBindingPreservesOnlyBoundedKnownFailureCodes(int status, string body, string expected)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent(body, Encoding.UTF8, "application/problem+json") };
        var error = await Assert.ThrowsAsync<AspException>(() => StorageAspRemoteAdapter.EnsureDocumentBindingSuccessAsync(response, default));
        Assert.AreEqual(expected, error.Code);
        Assert.DoesNotContain("PRIVATE", error.ToString());
    }

    [TestMethod]
    public async Task OversizedFailureDoesNotExposeOrAcceptRemoteDetails()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        { Content = new StringContent(new string('x', 8192)) };
        var error = await Assert.ThrowsAsync<AspException>(() => StorageAspRemoteAdapter.EnsureDocumentBindingSuccessAsync(response, default));
        Assert.AreEqual("provider_unavailable", error.Code);
    }
}
