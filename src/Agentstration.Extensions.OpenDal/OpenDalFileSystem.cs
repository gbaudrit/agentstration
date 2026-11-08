using System.Text.Json;
using Agentstration.Asp.Abstractions;
using OpenDAL;

namespace Agentstration.Extensions.OpenDal;

public static class OpenDalFileSystem
{
    public static OpenDalObjectProvider Create(string root, AspStoreRef store, OpenDalExecutionGate execution)
    {
        AspValidation.Require(Path.IsPathFullyQualified(root), "invalid_request", "An absolute operator-owned root is required.");
        var partition = Partition(store);
        var directory = Checked(Path.Combine(root, partition, "data"));
        Operator Open()
        {
            CreatePrivateDirectory(directory);
            // Without atomic_write_dir, native IfNotExists uses create_new/O_EXCL at open.
            // Publication is progressive, explicitly not advertised as atomic.
            return new("fs", new Dictionary<string, string> { ["root"] = directory });
        }
        void Check(string key)
        {
            foreach (var part in key.Split('/'))
                AspValidation.Require(!part.Any(c => Path.GetInvalidFileNameChars().Contains(c))
                    && (!OperatingSystem.IsWindows() || !part.EndsWith('.') && !part.EndsWith(' ')), "invalid_request", "The logical key is not representable on this filesystem.");
            var path = Path.GetFullPath(Path.Combine(directory, key.Replace('/', Path.DirectorySeparatorChar)));
            AspValidation.Require(path.StartsWith(directory + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), "forbidden", "The key escapes its configured root.");
            Checked(path);
        }
        return new(store, OpenDalStorageTypes.FileSystemId, OpenDalStorageTypes.ConfigurationSchema, Open, execution, Check);
    }
    public static string Partition(AspStoreRef store) => AspValidation.Digest(JsonSerializer.SerializeToUtf8Bytes(new { store.StoreId, store.BindingId }, AspProtocol.JsonOptions));
    private static void CreatePrivateDirectory(string path)
    {
        Checked(path);
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Checked(path);
    }
    private static string Checked(string path)
    {
        var absolute = Path.GetFullPath(path);
        for (var current = absolute; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            var linked = File.Exists(current) || Directory.Exists(current)
                ? (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0
                : new FileInfo(current).LinkTarget is not null;
            AspValidation.Require(!linked, "forbidden", "The configured filesystem location contains an unsupported link.");
        }
        return absolute;
    }
}
