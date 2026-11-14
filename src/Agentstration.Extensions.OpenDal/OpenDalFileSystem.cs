using System.Text.Json;
using System.Text;
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
            var path = Path.GetFullPath(Path.Combine(directory, EncodeKey(key).Replace('/', Path.DirectorySeparatorChar)));
            AspValidation.Require(path.StartsWith(directory + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal), "forbidden", "The key escapes its configured root.");
            Checked(path);
        }
        return new(store, OpenDalStorageTypes.FileSystemId, OpenDalStorageTypes.ConfigurationSchema, Open, execution, Check, EncodeKey, DecodeKey);
    }
    // A reversible per-key path, not a content index. Lowercase hex avoids Windows case,
    // reserved-name and trailing-dot aliases crossing ordinal ASP authorization boundaries.
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static string EncodeKey(string key)
    {
        string hex;
        try { hex = Convert.ToHexStringLower(Utf8.GetBytes(key)); }
        catch (EncoderFallbackException) { throw new AspException("invalid_request", "Invalid Unicode logical key."); }
        return string.Join('/', Enumerable.Range(0, (hex.Length + 119) / 120).Select(i => hex.Substring(i * 120, Math.Min(120, hex.Length - i * 120)))) + ".blob";
    }
    private static string? DecodeKey(string path)
    {
        if (!path.EndsWith(".blob", StringComparison.Ordinal)) return null;
        var hex = path[..^5].Replace("/", "", StringComparison.Ordinal);
        if (hex.Length is 0 or > 2048 || hex.Length % 2 != 0 || !hex.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) return null;
        try
        {
            var key = Utf8.GetString(Convert.FromHexString(hex));
            AspValidation.Key(key);
            return EncodeKey(key) == path ? key : null;
        }
        catch (Exception error) when (error is DecoderFallbackException or AspException) { return null; }
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
