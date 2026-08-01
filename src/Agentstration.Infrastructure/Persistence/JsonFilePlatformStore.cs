using System.Text.Json;

namespace Agentstration.Infrastructure.Persistence;

public sealed class JsonFilePlatformStore : InMemoryPlatformStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public JsonFilePlatformStore(string path)
    {
        _path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Data path must have a directory.");
        Directory.CreateDirectory(directory);
        if (File.Exists(_path))
        {
            State = JsonSerializer.Deserialize<PlatformState>(File.ReadAllText(_path), JsonOptions) ?? new PlatformState();
        }
    }

    protected override async Task ChangedAsync(CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            PlatformState snapshot;
            lock (Gate)
            {
                var json = JsonSerializer.Serialize(State, JsonOptions);
                snapshot = JsonSerializer.Deserialize<PlatformState>(json, JsonOptions) ?? new PlatformState();
            }

            var temporaryPath = _path + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(snapshot, JsonOptions), cancellationToken);
            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
