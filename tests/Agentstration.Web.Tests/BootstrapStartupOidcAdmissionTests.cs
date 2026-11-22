using System.Security.Claims;
using Agentstration.Identity.Contracts;
using Agentstration.Infrastructure;
using Agentstration.Web.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class BootstrapStartupOidcAdmissionTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExternalTicketsUseTheSameAdmissionPolicyBeforeSessionIssuance(bool allowed)
    {
        var services = new ServiceCollection();
        var startup = new BootstrapStartupState(); startup.Begin(); startup.EnableAdministration();
        services.AddSingleton(startup);
        services.AddBootstrapAuthenticationAdmission();
        services.AddSingleton<IAuthenticationAdmissionPolicy>(new Admission(allowed));
        services.AddSingleton<IPrincipalResolver>(new Resolver());
        await using var provider = services.BuildServiceProvider();
        var options = new OpenIdConnectOptions();
        foreach (var configure in provider.GetServices<IPostConfigureOptions<OpenIdConnectOptions>>())
            configure.PostConfigure(OpenIdConnectDefaults.AuthenticationScheme, options);
        var http = new DefaultHttpContext { RequestServices = provider };
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("iss", "https://issuer.invalid"), new Claim("sub", "fixture")], "oidc"));
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties(), "oidc");
        var context = new TicketReceivedContext(http,
            new AuthenticationScheme("oidc", "oidc", typeof(OpenIdConnectHandler)), options, ticket);
        await options.Events.OnTicketReceived(context);
        Assert.AreEqual(allowed, context.Result?.Failure is null);
        if (!allowed) Assert.AreEqual("authentication_unavailable", context.Result!.Failure!.Message);
    }
    private sealed class Admission(bool allowed) : IAuthenticationAdmissionPolicy
    {
        public Task<bool> CanSignInAsync(Guid principalId, CancellationToken cancellationToken) => Task.FromResult(allowed);
    }
    private sealed class Resolver : IPrincipalResolver
    {
        public Task<Principal?> ResolveAsync(string issuer, string subject, CancellationToken cancellationToken) =>
            Task.FromResult<Principal?>(new(Guid.NewGuid(), PrincipalKind.Human, "Fixture", null, PrincipalStatus.Active, DateTimeOffset.UnixEpoch));
        public Task<Principal?> ResolveLocalAsync(Guid accountId, CancellationToken cancellationToken) => Task.FromResult<Principal?>(null);
    }
}
