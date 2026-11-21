using System.Text.Json;
using System.Threading.Channels;
using Agentstration.Bootstrap.Contracts;
using Agentstration.Models;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Web.Hosting;

namespace Agentstration.Infrastructure.Bootstrap;

public sealed record BootstrapStartupModelOverrideOptions(string DirectoryPath);

internal sealed record BootstrapPendingModel(
    Guid AttemptId, string ResourceId, string ProfileDigest, ModelProfileResource Original,
    ResourceScopeRef OwnerScope, long Version, ResourceReference Provider, string Model, Guid? ProviderUid = null, bool Closed = false);

internal sealed record BootstrapSavedModelOverride(string ProfileDigest, long Version, Guid ProviderUid, BootstrapStartupModelSubstitution Change);

public sealed class BootstrapStartupModelOverrideStore(
    BootstrapStartupModelOverrideOptions options, BootstrapStartupState startup, TimeProvider clock) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, BootstrapPendingModel> pending = new(StringComparer.Ordinal);
    private Guid pendingAttempt;
    private readonly Channel<Guid> resume = Channel.CreateBounded<Guid>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    internal async Task<BootstrapPendingModel> RegisterAsync(ModelProfileResource profile, BootstrapResourceOperationContext operation,
        ResourceScopeRef owner, CancellationToken cancellationToken)
    {
        var id = operation.StartupResourceId ?? throw new InvalidOperationException("A startup resource identity is required.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            var attempt = startup.Read().AttemptId;
            if (pendingAttempt != attempt) { pending.Clear(); pendingAttempt = attempt; }
            var entry = new BootstrapPendingModel(attempt, id, operation.StartupProfileDigest!, profile,
                owner, 0, profile.Definition.Provider, profile.Definition.Model.Name);
            pending[id] = entry;
            var path = FilePath(id);
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                if (info.Length > 16384) throw new DeclarativeBootstrapException("bootstrap_substitution_invalid");
                await using var input = File.OpenRead(path);
                var saved = await JsonSerializer.DeserializeAsync<BootstrapSavedModelOverride>(input, JsonOptions, cancellationToken)
                    ?? throw new DeclarativeBootstrapException("bootstrap_substitution_invalid");
                if (saved.ProfileDigest == entry.ProfileDigest)
                {
                    entry = entry with { Version = saved.Version, Provider = saved.Change.Provider, Model = saved.Change.Model, ProviderUid = saved.ProviderUid };
                    pending[id] = entry;
                    startup.ObserveSubstitution(id, saved.Change);
                }
            }
            return entry;
        }
        finally { gate.Release(); }
    }

    internal async Task<BootstrapPendingModel> GetAsync(Guid attemptId, string id, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { return Required(attemptId, id); }
        finally { gate.Release(); }
    }

    private BootstrapPendingModel Required(Guid attempt, string id)
    {
        if (!pending.TryGetValue(id, out var entry) || entry.AttemptId != attempt || entry.Closed
            || startup.IsReady || startup.FindResource(attempt, id) is not { Kind: "ModelProfile", State: "running" or "waiting" or "failed" })
            throw new ResourceConcurrencyException("bootstrap_substitution_stale");
        return entry;
    }

    internal async Task<bool> TryCloseAsync(Guid attempt, string id, long version, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var entry = Required(attempt, id);
            if (entry.Version != version) return false;
            pending[id] = entry with { Closed = true };
            return true;
        }
        finally { gate.Release(); }
    }

    internal async Task SaveAsync(BootstrapPendingModel expected, ResourceReference provider, Guid providerUid,
        string model, Guid actor, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var entry = Required(expected.AttemptId, expected.ResourceId);
            if (entry.Version != expected.Version) throw new ResourceConcurrencyException("bootstrap_substitution_stale");
            var change = new BootstrapStartupModelSubstitution(entry.Provider, entry.Model, provider, model, actor, clock.GetUtcNow());
            var saved = new BootstrapSavedModelOverride(entry.ProfileDigest, checked(entry.Version + 1), providerUid, change);
            Directory.CreateDirectory(options.DirectoryPath);
            var destination = FilePath(entry.ResourceId);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                {
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    await JsonSerializer.SerializeAsync(output, saved, JsonOptions, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                }
                File.Move(temporary, destination, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            pending[entry.ResourceId] = entry with { Version = saved.Version, Provider = provider, Model = model, ProviderUid = providerUid };
            startup.ObserveSubstitution(entry.ResourceId, change);
            if (startup.Failure is not null || startup.FindResource(entry.AttemptId, entry.ResourceId)?.State == "failed")
                resume.Writer.TryWrite(entry.AttemptId);
        }
        finally { gate.Release(); }
    }

    public async Task<bool> HasCorrectableModelAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { return pending.Values.Any(entry => !entry.Closed && startup.FindResource(entry.AttemptId, entry.ResourceId)?.State == "failed"); }
        finally { gate.Release(); }
    }

    public async Task WaitForResumeAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        while (await resume.Reader.ReadAsync(cancellationToken) != attemptId) { }
    }

    private string FilePath(string id)
    {
        if (id.Length != 64 || id.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("Invalid Bootstrap resource identity.", nameof(id));
        return Path.Combine(options.DirectoryPath, id + ".json");
    }
    public void Dispose() => gate.Dispose();
}
