using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Agentstration.Extensions.Git;

public sealed class GitPublicationJournal(IOptions<GitOperationsOptions> options)
{
    internal async Task<Lease> OpenAsync(string key, CancellationToken token)
    {
        var root = Path.GetFullPath(options.Value.StateDirectory); Directory.CreateDirectory(root);
        var path = Path.Combine(root, key + ".json");
        for (var attempt = 0; attempt < 80; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return new(path, new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
            catch (IOException) when (attempt < 79) { await Task.Delay(50, token); }
            catch (IOException) { throw new GitOperationException("git_operation_busy"); }
        }
        throw new GitOperationException("git_operation_busy");
    }

    internal sealed class Lease(string path, FileStream stream) : IAsyncDisposable
    {
        internal async Task<GitPublicationIntent?> ReadAsync(CancellationToken token)
        {
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new GitOperationException("git_journal_invalid");
            await using var input = File.OpenRead(path);
            var entry = await JsonSerializer.DeserializeAsync<GitPublicationIntent>(input, cancellationToken: token);
            if (entry is null || entry.Phase is not ("prepared" or "sent")
                || entry.Phase == "sent" && !GitRepositoryConnection.Revision(entry.Candidate)
                || entry.Result?.Status is not (null or "Succeeded" or "Rejected" or "Conflict")) throw new GitOperationException("git_journal_invalid");
            return entry;
        }
        internal async Task SaveAsync(GitPublicationIntent intent, CancellationToken token)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(output, intent, cancellationToken: token);
                    await output.FlushAsync(token); output.Flush(true);
                }
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public ValueTask DisposeAsync() => stream.DisposeAsync();
    }
}
