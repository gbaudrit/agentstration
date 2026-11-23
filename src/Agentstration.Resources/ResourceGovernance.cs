using System.Text.Json.Serialization;

namespace Agentstration.Resources;

[JsonConverter(typeof(JsonStringEnumConverter<ResourceOriginKind>))]
public enum ResourceOriginKind
{
    [JsonStringEnumMemberName("user")] User,
    [JsonStringEnumMemberName("builtIn")] BuiltIn,
    [JsonStringEnumMemberName("pack")] Pack,
    [JsonStringEnumMemberName("extension")] Extension
}

[JsonConverter(typeof(JsonStringEnumConverter<ResourceManagementAuthorityKind>))]
public enum ResourceManagementAuthorityKind
{
    [JsonStringEnumMemberName("core")] Core,
    [JsonStringEnumMemberName("packInstallation")] PackInstallation,
    [JsonStringEnumMemberName("extensionInstallation")] ExtensionInstallation
}

public sealed record ResourceOrigin
{
    public required ResourceOriginKind Kind { get; init; }
    public string? Reference { get; init; }
    public string? Version { get; init; }
}

public sealed record ResourceManagementAuthority
{
    public required ResourceManagementAuthorityKind Kind { get; init; }
    public required string Reference { get; init; }
    public string? Version { get; init; }
}

public sealed record ResourceBootstrapApplication(
    string ProfileId, string Profile, string Version, Guid ApplicationId,
    ResourceScopeRef TargetScope, string Location);

public sealed record ResourceGovernance
{
    public static ResourceGovernance User { get; } = new()
    {
        Origin = new ResourceOrigin { Kind = ResourceOriginKind.User }
    };

    public required ResourceOrigin Origin { get; init; }
    public ResourceManagementAuthority? ManagedBy { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResourceBootstrapApplication? AppliedBy { get; init; }

    [JsonIgnore]
    public bool IsManaged => ManagedBy is not null;

    public static ResourceGovernance Core(string component, string? version = null) => new()
    {
        Origin = new ResourceOrigin
        {
            Kind = ResourceOriginKind.BuiltIn,
            Reference = component,
            Version = version
        },
        ManagedBy = new ResourceManagementAuthority
        {
            Kind = ResourceManagementAuthorityKind.Core,
            Reference = component,
            Version = version
        }
    };

    public static ResourceGovernance Pack(string installationReference, string packReference, string version) => new()
    {
        Origin = new ResourceOrigin
        {
            Kind = ResourceOriginKind.Pack,
            Reference = packReference,
            Version = version
        },
        ManagedBy = new ResourceManagementAuthority
        {
            Kind = ResourceManagementAuthorityKind.PackInstallation,
            Reference = installationReference,
            Version = version
        }
    };

    public static ResourceGovernance Extension(string installationReference, string extensionReference, string? version) => new()
    {
        Origin = new ResourceOrigin
        {
            Kind = ResourceOriginKind.Extension,
            Reference = extensionReference,
            Version = version
        },
        ManagedBy = new ResourceManagementAuthority
        {
            Kind = ResourceManagementAuthorityKind.ExtensionInstallation,
            Reference = installationReference,
            Version = version
        }
    };
}

public interface IResourceGovernanceContext
{
    ResourceGovernance? Current { get; }
    IDisposable Push(ResourceGovernance governance);
    IDisposable PushBootstrap(ResourceBootstrapApplication application, Func<string, ResourceNamespace, string, ResourceGovernance?, bool> matches,
        Action<ResourceScopeRef, ResourceNamespace, string, string, Guid?> observe);
    ResourceBootstrapApplication? GetBootstrapApplication(string kind, ResourceNamespace @namespace, string name);
    void ObserveBootstrapCreation(ResourceScopeRef scope, ResourceNamespace @namespace, string kind, string name, Guid? uid);
}

public sealed class ResourceGovernanceContext : IResourceGovernanceContext
{
    private readonly AsyncLocal<ResourceGovernance?> current = new();

    private readonly AsyncLocal<BootstrapScope?> bootstrap = new();

    public ResourceGovernance? Current => current.Value;

    public IDisposable PushBootstrap(ResourceBootstrapApplication application, Func<string, ResourceNamespace, string, ResourceGovernance?, bool> matches,
        Action<ResourceScopeRef, ResourceNamespace, string, string, Guid?> observe)
    {
        var previous = bootstrap.Value;
        var frame = new BootstrapScope(application, matches, observe);
        bootstrap.Value = frame;
        return new RestoreBootstrap(bootstrap, frame, previous);
    }

    public ResourceBootstrapApplication? GetBootstrapApplication(string kind, ResourceNamespace @namespace, string name)
    {
        var frame = bootstrap.Value;
        if (frame is null || !frame.Active) return null;
        return frame.Matches(kind, @namespace, name, Current) ? frame.Application : null;
    }

    public void ObserveBootstrapCreation(ResourceScopeRef scope, ResourceNamespace @namespace, string kind, string name, Guid? uid)
    {
        if (GetBootstrapApplication(kind, @namespace, name) is not null)
            bootstrap.Value!.Observe(scope, @namespace, kind, name, uid);
    }

    private sealed record BootstrapScope(ResourceBootstrapApplication Application,
        Func<string, ResourceNamespace, string, ResourceGovernance?, bool> Matches,
        Action<ResourceScopeRef, ResourceNamespace, string, string, Guid?> Observe)
    {
        public volatile bool Active = true;
    }

    private sealed class RestoreBootstrap(AsyncLocal<BootstrapScope?> slot, BootstrapScope frame, BootstrapScope? previous) : IDisposable
    {
        public void Dispose()
        {
            frame.Active = false;
            slot.Value = previous;
        }
    }

    public IDisposable Push(ResourceGovernance governance)
    {
        ArgumentNullException.ThrowIfNull(governance);
        if (!governance.IsManaged)
            throw new ArgumentException("A reconciliation context requires an exact management authority.", nameof(governance));
        var previous = current.Value;
        current.Value = governance;
        return new PopScope(current, previous);
    }

    private sealed class PopScope(AsyncLocal<ResourceGovernance?> current, ResourceGovernance? previous) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed) return;
            current.Value = previous;
            disposed = true;
        }
    }
}

public sealed class ResourceGovernanceViolationException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

public static class ResourceGovernanceEnforcement
{
    public static ResourceGovernance PrepareCreate(ResourceGovernance requested, ResourceGovernance? authority,
        ResourceBootstrapApplication? appliedBy = null)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (authority is null)
        {
            if (requested != ResourceGovernance.User)
                throw Violation("resource_governance_reserved", "Resource governance is assigned only by a trusted management authority.");
            return ResourceGovernance.User with { AppliedBy = appliedBy };
        }

        EnsureManagedAuthority(authority);
        if (requested != ResourceGovernance.User && requested != authority)
            throw Violation("resource_governance_authority_mismatch", "The supplied governance does not match the active management authority.");
        return authority with { AppliedBy = appliedBy };
    }

    public static ResourceGovernance PrepareUpdate(ResourceGovernance existing, ResourceGovernance requested, ResourceGovernance? authority)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(requested);
        if (requested.AppliedBy is not null && requested.AppliedBy != existing.AppliedBy)
            throw Violation("resource_governance_immutable", "Resource application provenance is immutable.");
        requested = requested with { AppliedBy = null };
        if (!existing.IsManaged)
        {
            if (requested != ResourceGovernance.User || authority is not null)
                throw Violation("resource_governance_adoption_forbidden", "An existing user-managed resource cannot be adopted through an update.");
            return existing;
        }

        EnsureManager(existing, authority);
        if (requested != ResourceGovernance.User && !SameIdentity(requested, existing))
            throw Violation("resource_governance_immutable", "Resource origin and management authority are immutable.");
        return authority! with { AppliedBy = existing.AppliedBy };
    }

    public static void EnsureManager(ResourceGovernance governance, ResourceGovernance? authority)
    {
        ArgumentNullException.ThrowIfNull(governance);
        if (!governance.IsManaged) return;
        if (authority?.ManagedBy is not { } manager
            || manager.Kind != governance.ManagedBy!.Kind
            || !string.Equals(manager.Reference, governance.ManagedBy.Reference, StringComparison.Ordinal))
            throw Violation("resource_managed", $"Resource content and lifecycle are managed by '{governance.ManagedBy!.Reference}'.");
    }

    private static void EnsureManagedAuthority(ResourceGovernance authority)
    {
        if (!authority.IsManaged)
            throw new ArgumentException("The active reconciliation context has no management authority.", nameof(authority));
    }

    private static bool SameIdentity(ResourceGovernance left, ResourceGovernance right) =>
        left.Origin.Kind == right.Origin.Kind
        && string.Equals(left.Origin.Reference, right.Origin.Reference, StringComparison.Ordinal)
        && left.ManagedBy?.Kind == right.ManagedBy?.Kind
        && string.Equals(left.ManagedBy?.Reference, right.ManagedBy?.Reference, StringComparison.Ordinal);

    private static ResourceGovernanceViolationException Violation(string code, string message) => new(code, message);
}
