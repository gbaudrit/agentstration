using Agentstration.Asp.Abstractions;
using Agentstration.Storage.Contracts;

namespace Agentstration.Infrastructure.Storage;

public sealed record LocalStorageOptions(string RootDirectory)
{
    public const string ConfigurationKey = "Agentstration:LocalStorage:RootDirectory";
}

/// <summary>Only operator configuration and exact scoped UIDs participate in placement.</summary>
public sealed class LocalStoragePaths(LocalStorageOptions options)
{
    public string DirectoryFor(StorageRevisionReference storage)
    {
        if (storage.StorageUid == Guid.Empty)
            throw new AspException("invalid_request", "An exact Storage identity is required.");
        var scope = AspValidation.Digest(System.Text.Encoding.UTF8.GetBytes(storage.Storage.ScopeRef.Value));
        return Checked(Path.Combine(options.RootDirectory, scope, storage.StorageUid.ToString("N")));
    }

    public string EnsureDirectoryFor(StorageRevisionReference storage)
    {
        var directory = DirectoryFor(storage);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return Checked(directory);
    }

    public static string Checked(string path)
    {
        var absolute = Path.GetFullPath(path);
        for (var current = absolute; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
                AspValidation.Require((File.GetAttributes(current) & FileAttributes.ReparsePoint) == 0,
                    "storage_path_unsafe", "The configured storage location contains an unsupported link.");
            else
            {
                var entry = new FileInfo(current);
                AspValidation.Require(entry.LinkTarget is null, "storage_path_unsafe", "The configured storage location contains an unsupported link.");
            }
        }
        return absolute;
    }
}
