using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agentstration.Management.Abstractions;
using Agentstration.Management.Core;
using Agentstration.Security.AspNetCoreIdentity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Management.Tests;

[TestClass]
public sealed partial class SecurityApiTests
{
    private const string LocalPassword = "A-strong-local-password-42!";
    private const string ChangedLocalPassword = "A-changed-local-password-84!";

    private static WebApplicationFactory<Program> Factory(string mode) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Agentstration:Authentication:Mode", mode);
        });

    private static HttpClient UnredirectedClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    private sealed record PreferencesResponse(string Theme, string? Language, DateTimeOffset UpdatedAt);

    private static async Task BootstrapAsync(HttpClient client, string userName)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/bootstrap", new
        {
            userName,
            password = LocalPassword,
            displayName = "Security test user",
            topology = BootstrapTopology()
        });
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
    }

    private static LocalBootstrapTopology BootstrapTopology() =>
        new("test", "Test tenant", "default", "Default workspace");

    private static async Task LoginAsync(HttpClient client, string userName, string password)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/local/login", new { userName, password });
        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<LocalAccountView> CreateAccountAsync(HttpClient administrator, Guid workspaceId, string userName, string role)
    {
        using var response = await administrator.PostAsJsonAsync("/api/identity/accounts/", new
        {
            userName,
            password = LocalPassword,
            displayName = userName,
            workspaceId,
            role
        });
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<LocalAccountView>()
            ?? throw new InvalidOperationException("The created local account response was empty.");
    }

    private static HttpRequestMessage Json(HttpMethod method, string uri, string json) => new(method, uri)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    private static async Task AssertAccessMatrixAsync(HttpClient client, IReadOnlyList<AccessProbe> probes, IReadOnlyList<bool> expected)
    {
        for (var index = 0; index < probes.Count; index++)
        {
            using var response = await client.SendAsync(probes[index].Request());
            if (expected[index])
            {
                Assert.AreNotEqual(HttpStatusCode.Unauthorized, response.StatusCode, $"{probes[index].Name} unexpectedly required authentication.");
                Assert.AreNotEqual(HttpStatusCode.Forbidden, response.StatusCode, $"{probes[index].Name} unexpectedly denied authorization.");
            }
            else
            {
                Assert.IsTrue(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                    $"{probes[index].Name} unexpectedly crossed the authorization boundary with status {(int)response.StatusCode}.");
            }
        }
    }

    private sealed record AccessProbe(string Name, Func<HttpRequestMessage> Request);

    private static FormUrlEncodedContent ChangePasswordForm(
        string? antiforgeryToken,
        string currentPassword,
        string newPassword)
    {
        var values = new Dictionary<string, string>
        {
            ["Input.CurrentPassword"] = currentPassword,
            ["Input.NewPassword"] = newPassword,
            ["Input.ConfirmPassword"] = newPassword
        };
        if (antiforgeryToken is not null) values["__RequestVerificationToken"] = antiforgeryToken;
        return new FormUrlEncodedContent(values);
    }

    private static string AntiforgeryToken(string html)
    {
        var match = Regex.Match(
            html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"",
            RegexOptions.CultureInvariant);
        Assert.IsTrue(match.Success, "The rendered form must contain an antiforgery token.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private sealed record BootstrapStatus(bool Initialized);
}
