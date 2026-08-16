using System.Security.Claims;
using Agentstration.Management.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
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

public sealed record LocalBootstrapRequest(string UserName, string Password, string DisplayName, string? Email);
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

public sealed class LocalBootstrapCoordinator(
    UserManager<LocalIdentityUser> users,
    LocalIdentityDbContext database,
    IInitialPrincipalProvisioner provisioner,
    ISecurityAuditWriter audit,
    TimeProvider timeProvider,
    LocalBootstrapLock bootstrapLock)
{
    public async Task<bool> IsInitializedAsync(CancellationToken cancellationToken) =>
        await database.BootstrapStates.AsNoTracking().AnyAsync(cancellationToken)
        || await users.Users.AnyAsync(cancellationToken);

    public async Task<LocalBootstrapResult> BootstrapAsync(LocalBootstrapRequest request, CancellationToken cancellationToken)
    {
        await bootstrapLock.Semaphore.WaitAsync(cancellationToken);
        try
        {
            if (await IsInitializedAsync(cancellationToken))
                return new(false, null, ["This Agentstration instance is already initialized."]);
            if (string.IsNullOrWhiteSpace(request.UserName) || request.UserName.Trim().Length is < 3 or > 64)
                return new(false, null, ["UserName must contain between 3 and 64 characters."]);
            if (string.IsNullOrWhiteSpace(request.Password))
                return new(false, null, ["Password is required."]);
            if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length is < 2 or > 120)
                return new(false, null, ["DisplayName must contain between 2 and 120 characters."]);

            var accountId = Guid.NewGuid();
            var principalId = Guid.NewGuid();
            var account = new LocalIdentityUser
            {
                Id = accountId,
                UserName = request.UserName.Trim(),
                Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
                LockoutEnabled = true
            };
            var created = await users.CreateAsync(account, request.Password);
            if (!created.Succeeded)
                return new(false, null, created.Errors.Select(error => error.Description).ToArray());

            try
            {
                await provisioner.ProvisionAsync(new InitialPrincipalProvisioning(
                    accountId,
                    principalId,
                    request.DisplayName.Trim(),
                    account.Email), cancellationToken);
                database.BootstrapStates.Add(new BootstrapState
                {
                    Id = 1,
                    PrincipalId = principalId,
                    CompletedAt = timeProvider.GetUtcNow()
                });
                await database.SaveChangesAsync(cancellationToken);
                await audit.WriteAsync(new(
                    SecurityAuditActions.InstanceBootstrapped,
                    ActorPrincipalId: principalId,
                    TargetPrincipalId: principalId,
                    TargetAccountId: accountId), cancellationToken);
                return new(true, principalId, []);
            }
            catch
            {
                await users.DeleteAsync(account);
                throw;
            }
        }
        finally
        {
            bootstrapLock.Semaphore.Release();
        }
    }
}

public sealed class LocalAccountAdministrationService(
    UserManager<LocalIdentityUser> users,
    IIdentityStore identityStore,
    ILocalPrincipalProvisioner provisioner,
    ICurrentRequestContext requestContext,
    IPlatformAuthorizationService platformAuthorization,
    ISecurityAuditWriter audit)
{
    public async Task<IReadOnlyList<LocalAccountView>> ListAsync(CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(cancellationToken);
        var result = new List<LocalAccountView>();
        foreach (var account in await users.Users.AsNoTracking().OrderBy(value => value.UserName).ToArrayAsync(cancellationToken))
        {
            var link = await identityStore.FindLocalIdentityAsync(account.Id, cancellationToken);
            if (link is null) continue;
            var principal = await identityStore.GetPrincipalAsync(link.PrincipalId, cancellationToken);
            if (principal is null) continue;
            result.Add(await ViewAsync(account, principal, cancellationToken));
        }
        return result;
    }

    public async Task<(LocalAccountView? Account, IReadOnlyList<string> Errors)> CreateAsync(CreateLocalAccountRequest request, CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(request.UserName) || request.UserName.Trim().Length is < 3 or > 64)
            return (null, ["UserName must contain between 3 and 64 characters."]);
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length is < 2 or > 120)
            return (null, ["DisplayName must contain between 2 and 120 characters."]);
        if (string.IsNullOrWhiteSpace(request.Password)) return (null, ["Password is required."]);

        var account = new LocalIdentityUser
        {
            Id = Guid.NewGuid(),
            UserName = request.UserName.Trim(),
            Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
            LockoutEnabled = true
        };
        var created = await users.CreateAsync(account, request.Password);
        if (!created.Succeeded) return (null, created.Errors.Select(error => error.Description).ToArray());
        try
        {
            var principal = await provisioner.ProvisionAsync(new LocalPrincipalProvisioning(
                account.Id,
                Guid.NewGuid(),
                request.WorkspaceId,
                request.Role,
                request.DisplayName.Trim(),
                account.Email), cancellationToken);
            await audit.WriteAsync(new(
                SecurityAuditActions.LocalAccountCreated,
                TargetPrincipalId: principal.Id,
                TargetAccountId: account.Id,
                WorkspaceId: request.WorkspaceId), cancellationToken);
            return (await ViewAsync(account, principal, cancellationToken), []);
        }
        catch
        {
            await users.DeleteAsync(account);
            throw;
        }
    }

    public async Task<LocalAccountView> SetEnabledAsync(Guid accountId, bool enabled, CancellationToken cancellationToken)
    {
        await EnsurePlatformAdministratorAsync(cancellationToken);
        var account = await users.FindByIdAsync(accountId.ToString("D"))
            ?? throw new ArgumentException("The local account does not exist.", nameof(accountId));
        var link = await identityStore.FindLocalIdentityAsync(account.Id, cancellationToken)
            ?? throw new InvalidOperationException("The local account is not linked to a Principal.");
        var principal = await identityStore.GetPrincipalAsync(link.PrincipalId, cancellationToken)
            ?? throw new InvalidOperationException("The linked Principal does not exist.");
        if (!enabled && await platformAuthorization.IsPlatformAdministratorAsync(principal.Id, cancellationToken))
            throw new InvalidOperationException("A Platform administrator account cannot be disabled in this iteration.");

        account.LockoutEnabled = true;
        account.LockoutEnd = enabled ? null : DateTimeOffset.MaxValue;
        var updated = await users.UpdateAsync(account);
        if (!updated.Succeeded) throw new InvalidOperationException(string.Join("; ", updated.Errors.Select(error => error.Description)));
        await users.UpdateSecurityStampAsync(account);
        principal = principal with { Status = enabled ? PrincipalStatus.Active : PrincipalStatus.Disabled };
        await identityStore.UpdatePrincipalAsync(principal, cancellationToken);
        await audit.WriteAsync(new(
            enabled ? SecurityAuditActions.LocalAccountEnabled : SecurityAuditActions.LocalAccountDisabled,
            TargetPrincipalId: principal.Id,
            TargetAccountId: account.Id), cancellationToken);
        return await ViewAsync(account, principal, cancellationToken);
    }

    private async Task EnsurePlatformAdministratorAsync(CancellationToken cancellationToken)
    {
        if (!requestContext.IsInitialized || !await platformAuthorization.IsPlatformAdministratorAsync(requestContext.Current.PrincipalId, cancellationToken))
            throw new AuthorizationDeniedException("platform/admin");
    }

    private async Task<LocalAccountView> ViewAsync(LocalIdentityUser account, Principal principal, CancellationToken cancellationToken) =>
        new(account.Id, principal.Id, account.UserName ?? account.Id.ToString("D"), principal.DisplayName, principal.Email,
            principal.Status, account.AccessFailedCount, account.LockoutEnd,
            await platformAuthorization.IsPlatformAdministratorAsync(principal.Id, cancellationToken));
}

public sealed class LocalAccountSecurityService(
    UserManager<LocalIdentityUser> users,
    SignInManager<LocalIdentityUser> signIn,
    IIdentityStore identityStore,
    ISecurityAuditWriter audit)
{
    public async Task<LocalAccountSecurityView?> GetAsync(Guid accountId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var account = await users.FindByIdAsync(accountId.ToString("D"));
        return account is null
            ? null
            : new(account.Id, account.UserName ?? account.Id.ToString("D"), account.Email);
    }

    public async Task<LocalAccountSecurityResult> ChangePasswordAsync(
        Guid accountId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var account = await users.FindByIdAsync(accountId.ToString("D"));
        if (account is null)
        {
            await audit.WriteAsync(new(SecurityAuditActions.LocalPasswordChanged, SecurityAuditOutcome.Failed,
                TargetAccountId: accountId, ReasonCode: "account-not-found"), cancellationToken);
            return new(false, ["The local account no longer exists."]);
        }
        var changed = await users.ChangePasswordAsync(account, currentPassword, newPassword);
        if (!changed.Succeeded)
        {
            await audit.WriteAsync(new(SecurityAuditActions.LocalPasswordChanged, SecurityAuditOutcome.Failed,
                TargetPrincipalId: await PrincipalIdAsync(accountId, cancellationToken), TargetAccountId: accountId,
                ReasonCode: "credential-rejected"), cancellationToken);
            return new(false, changed.Errors.Select(error => error.Description).ToArray());
        }

        await signIn.RefreshSignInAsync(account);
        await audit.WriteAsync(new(SecurityAuditActions.LocalPasswordChanged,
            TargetPrincipalId: await PrincipalIdAsync(accountId, cancellationToken), TargetAccountId: accountId), cancellationToken);
        return new(true, []);
    }

    public async Task<LocalAccountSecurityResult> SignOutOtherSessionsAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var account = await users.FindByIdAsync(accountId.ToString("D"));
        if (account is null)
        {
            await audit.WriteAsync(new(SecurityAuditActions.LocalSessionsRevoked, SecurityAuditOutcome.Failed,
                TargetAccountId: accountId, ReasonCode: "account-not-found"), cancellationToken);
            return new(false, ["The local account no longer exists."]);
        }
        var updated = await users.UpdateSecurityStampAsync(account);
        if (!updated.Succeeded)
        {
            await audit.WriteAsync(new(SecurityAuditActions.LocalSessionsRevoked, SecurityAuditOutcome.Failed,
                TargetPrincipalId: await PrincipalIdAsync(accountId, cancellationToken), TargetAccountId: accountId,
                ReasonCode: "security-stamp-update-failed"), cancellationToken);
            return new(false, updated.Errors.Select(error => error.Description).ToArray());
        }

        await signIn.RefreshSignInAsync(account);
        await audit.WriteAsync(new(SecurityAuditActions.LocalSessionsRevoked,
            TargetPrincipalId: await PrincipalIdAsync(accountId, cancellationToken), TargetAccountId: accountId), cancellationToken);
        return new(true, []);
    }

    private async Task<Guid?> PrincipalIdAsync(Guid accountId, CancellationToken cancellationToken) =>
        (await identityStore.FindLocalIdentityAsync(accountId, cancellationToken))?.PrincipalId;
}

public sealed class LocalAuthenticationService(
    UserManager<LocalIdentityUser> users,
    SignInManager<LocalIdentityUser> signIn,
    IIdentityStore identityStore,
    ISecurityAuditWriter audit)
{
    public async Task<LocalLoginOutcome> PasswordSignInAsync(
        string userName,
        string password,
        bool rememberMe,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            await audit.WriteAsync(new(SecurityAuditActions.LocalLogin, SecurityAuditOutcome.Failed,
                ReasonCode: "missing-credentials"), cancellationToken);
            return LocalLoginOutcome.Failed;
        }
        var normalized = userName.Trim();
        var result = await signIn.PasswordSignInAsync(normalized, password, rememberMe, lockoutOnFailure: true);
        var account = await users.FindByNameAsync(normalized);
        var principalId = account is null
            ? null
            : (await identityStore.FindLocalIdentityAsync(account.Id, cancellationToken))?.PrincipalId;
        var outcome = result.Succeeded ? LocalLoginOutcome.Succeeded : result.IsLockedOut ? LocalLoginOutcome.LockedOut : LocalLoginOutcome.Failed;
        await audit.WriteAsync(new(
            SecurityAuditActions.LocalLogin,
            result.Succeeded ? SecurityAuditOutcome.Succeeded : SecurityAuditOutcome.Failed,
            ActorPrincipalId: result.Succeeded ? principalId : null,
            TargetPrincipalId: principalId,
            TargetAccountId: account?.Id,
            ReasonCode: outcome switch
            {
                LocalLoginOutcome.LockedOut => "locked-out",
                LocalLoginOutcome.Failed => "invalid-credentials",
                _ => null
            }), cancellationToken);
        return outcome;
    }

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        await signIn.SignOutAsync();
        await audit.WriteAsync(new(SecurityAuditActions.LocalLogout), cancellationToken);
    }
}

public sealed class LocalBootstrapLock
{
    internal SemaphoreSlim Semaphore { get; } = new(1, 1);
}

public sealed class LocalIdentityDatabaseInitializer(IServiceScopeFactory scopeFactory)
{
    public const string InitialMigration = "20260816100707_InitialIdentity";
    private static readonly string[] LegacyTables =
    [
        "AgentstrationBootstrap", "AspNetRoleClaims", "AspNetRoles", "AspNetUserClaims",
        "AspNetUserLogins", "AspNetUserRoles", "AspNetUsers", "AspNetUserTokens"
    ];

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<LocalIdentityDbContext>();
        await BaselineLegacyDatabaseAsync(database, cancellationToken);
        await database.Database.MigrateAsync(cancellationToken);
    }

    private static async Task BaselineLegacyDatabaseAsync(LocalIdentityDbContext database, CancellationToken cancellationToken)
    {
        await database.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var existing = new HashSet<string>(StringComparer.Ordinal);
            await using (var command = database.Database.GetDbConnection().CreateCommand())
            {
                command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) existing.Add(reader.GetString(0));
            }

            if (!existing.Contains("AspNetUsers")) return;
            var missing = LegacyTables.Where(table => !existing.Contains(table)).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException(
                    $"The existing Identity database has a partial legacy schema. Missing tables: {string.Join(", ", missing)}.");

            await database.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                    "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                    "ProductVersion" TEXT NOT NULL
                );
                """,
                cancellationToken);
            await database.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT OR IGNORE INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({InitialMigration}, {ProductInfo.GetVersion()})",
                cancellationToken);
        }
        finally
        {
            await database.Database.CloseConnectionAsync();
        }
    }
}

public static class LocalIdentityServiceCollectionExtensions
{
    public static IServiceCollection AddAgentstrationLocalIdentity(
        this IServiceCollection services,
        string connectionString,
        string dataProtectionKeysPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataProtectionKeysPath);
        var keysDirectory = PrepareDataProtectionKeysDirectory(dataProtectionKeysPath);
        services.AddDataProtection()
            .SetApplicationName("Agentstration")
            .PersistKeysToFileSystem(keysDirectory);
        services.AddDbContext<LocalIdentityDbContext>(options => options.UseSqlite(connectionString));
        services.AddIdentityCore<LocalIdentityUser>(options =>
            {
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.User.RequireUniqueEmail = false;
            })
            .AddSignInManager()
            .AddEntityFrameworkStores<LocalIdentityDbContext>()
            .AddDefaultTokenProviders();
        services.AddScoped<IUserClaimsPrincipalFactory<LocalIdentityUser>, LocalIdentityClaimsPrincipalFactory>();
        services.AddScoped<LocalBootstrapCoordinator>();
        services.AddScoped<LocalAccountAdministrationService>();
        services.AddScoped<LocalAccountSecurityService>();
        services.AddScoped<LocalAuthenticationService>();
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);
        services.AddSingleton<LocalBootstrapLock>();
        services.AddSingleton<LocalIdentityDatabaseInitializer>();
        return services;
    }

    private static DirectoryInfo PrepareDataProtectionKeysDirectory(string path)
    {
        try
        {
            var directory = new DirectoryInfo(Path.GetFullPath(path));
            directory.Create();
            directory.Refresh();
            var probe = Path.Combine(directory.FullName, $".agentstration-write-test-{Guid.NewGuid():N}");
            using (new FileStream(probe, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 1,
                Options = FileOptions.DeleteOnClose
            })) { }
            return directory;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new InvalidOperationException("The configured Data Protection keys path is not a writable directory.", exception);
        }
    }
}
