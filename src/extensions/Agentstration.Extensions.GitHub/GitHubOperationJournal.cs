using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Agentstration.Extensions.GitHub;

public sealed class GitHubProviderOptions
{
    public string StateDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "data", "github-operations");
    public string[] AllowedApiOrigins { get; set; } = ["https://api.github.com"];
    public bool AllowHttpLoopback { get; set; }
}

public sealed class GitHubOperationJournal(IOptions<GitHubProviderOptions> options)
{
    internal async Task<T> RunAsync<T>(string key, string digest, string authority, string operation, JsonElement input,
        string mode, Func<bool, CancellationToken, Task<T>> action, Func<T, CancellationToken, Task> authorizeReplay, Func<string, T> missing, CancellationToken token)
    {
        if (!Coordinate(key, digest, mode)) throw new GitHubProviderException("github_operation_invalid");
        var directory = Path.GetFullPath(options.Value.StateDirectory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, key + ".json");
        await using var lease = await LockAsync(path + ".lock", token);
        GitHubJournal? entry = null;
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 512 * 1024) throw new GitHubProviderException("github_journal_invalid");
            await using var stream = File.OpenRead(path);
            entry = await JsonSerializer.DeserializeAsync<GitHubJournal>(stream, cancellationToken: token)
                ?? throw new GitHubProviderException("github_journal_invalid");
            if (entry.Phase != "sent") throw new GitHubProviderException("github_journal_invalid");
            if (entry.IntentDigest != digest || entry.Authority != authority || entry.Operation != operation || entry.Input.GetRawText() != input.GetRawText())
                throw new GitHubProviderException("github_operation_conflict");
            if (entry.Result is { } result)
            {
                var receipt = result.Deserialize<T>() ?? throw new GitHubProviderException("github_journal_invalid");
                await authorizeReplay(receipt, token);
                return receipt;
            }
        }
        if (entry is null && mode == "recover") return missing("Missing");
        var recovering = entry?.Phase == "sent";
        if (!recovering)
        {
            // Write-ahead persistence: a crash from this point cannot authorize a second remote write.
            entry = new(digest, authority, operation, input, "sent");
            await SaveAsync(path, entry, token);
        }
        var output = await action(recovering, token);
        var encoded = JsonSerializer.SerializeToElement(output);
        // Uncertain receipts stay recoverable; all other receipts are terminal and replay exactly.
        if (encoded.TryGetProperty("Status", out var status) && status.GetString() != "Uncertain")
            await SaveAsync(path, entry! with { Result = encoded }, token);
        return output;
    }

    private static async Task SaveAsync(string path, GitHubJournal entry, CancellationToken token)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, entry, cancellationToken: token);
                await stream.FlushAsync(token);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task<FileStream> LockAsync(string path, CancellationToken token)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 79) { await Task.Delay(50, token); }
        }
        throw new GitHubProviderException("github_operation_busy");
    }

    internal static bool Coordinate(string key, string digest, string mode) => Hex(key) && Hex(digest) && mode is "execute" or "recover";
    private static bool Hex(string value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
