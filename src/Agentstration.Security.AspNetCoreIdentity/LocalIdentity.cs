using System.Security.Claims;
using Agentstration.Management.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agentstration.Security.AspNetCoreIdentity;

public static class LocalIdentityClaimTypes
{
    public const string AccountId = "agentstration:local_account_id";
}
public sealed class LocalIdentityUser : IdentityUser<Guid> { }

public sealed class LocalIdentityDbContext(DbContextOptions<LocalIdentityDbContext> options)
    : IdentityDbContext<LocalIdentityUser, IdentityRole<Guid>, Guid>(options)
{
    internal DbSet<BootstrapState> BootstrapStates => Set<BootstrapState>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<BootstrapState>().ToTable("AgentstrationBootstrap").HasKey(value => value.Id);
    }
}

public sealed class LocalIdentityDbContextFactory : IDesignTimeDbContextFactory<LocalIdentityDbContext>
{
    public LocalIdentityDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LocalIdentityDbContext>()
            .UseSqlite("Data Source=identity-design.db")
            .Options;
        return new LocalIdentityDbContext(options);
    }
}

internal sealed class BootstrapState
{
    public int Id { get; set; }
    public Guid PrincipalId { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
}

public sealed class LocalIdentityClaimsPrincipalFactory(
    UserManager<LocalIdentityUser> userManager,
    IOptions<IdentityOptions> optionsAccessor)
    : UserClaimsPrincipalFactory<LocalIdentityUser>(userManager, optionsAccessor)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(LocalIdentityUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(LocalIdentityClaimTypes.AccountId, user.Id.ToString("D")));
        return identity;
    }
}

public sealed record LocalBootstrapTopology(
    string TenantName,
    string TenantDisplayName,
    string WorkspaceName,
    string WorkspaceDisplayName);

public sealed record LocalBootstrapRequest(
    string UserName,
    string Password,
    string DisplayName,
    string? Email,
    LocalBootstrapTopology? Topology = null);
public sealed record LocalBootstrapResult(bool Succeeded, Guid? PrincipalId, IReadOnlyList<string> Errors);
public sealed record CreateLocalAccountRequest(string UserName, string Password, string DisplayName, string? Email, Guid WorkspaceId, string Role);
public sealed record LocalAccountView(
    Guid AccountId,
    Guid PrincipalId,
    string UserName,
    string DisplayName,
    string? Email,
    PrincipalStatus PrincipalStatus,
    int AccessFailedCount,
    DateTimeOffset? LockoutEnd,
    bool PlatformAdministrator);
public sealed record LocalAccountSecurityView(Guid AccountId, string UserName, string? Email);
public sealed record LocalAccountSecurityResult(bool Succeeded, IReadOnlyList<string> Errors);
public enum LocalLoginOutcome { Succeeded, Failed, LockedOut }
