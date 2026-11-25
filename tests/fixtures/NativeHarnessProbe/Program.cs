using System.Security.Cryptography;
using System.Text.Json;

// Offline process-boundary fixture, deliberately without Agentstration references or a network client.
// It verifies the exported inventory. It does not emulate Codex or claim a model was invoked.
if (args.Length != 1 || args[0] != "--verify") return 2;
var root = Directory.GetCurrentDirectory();
var manifest = Path.Combine(root, "agentstration-harness.provenance.json");
if (!File.Exists(manifest)) return 3;
using var provenance = JsonDocument.Parse(await File.ReadAllBytesAsync(manifest));
foreach (var file in provenance.RootElement.GetProperty("fileDigests").EnumerateObject())
{
    if (file.Name is not ("AGENTS.md" or ".codex/config.toml")) return 4;
    var path = Path.Combine(root, file.Name);
    if (!File.Exists(path) || new FileInfo(path).Length > 32 * 1024) return 5;
    var digest = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)));
    if (digest != file.Value.GetString()) return 6;
}
Console.WriteLine(JsonSerializer.Serialize(new
{
    mode = "offline-inventory-probe",
    version = provenance.RootElement.GetProperty("profileVersion").GetString(),
    instructions = await File.ReadAllTextAsync(Path.Combine(root, "AGENTS.md")),
    configuration = await File.ReadAllTextAsync(Path.Combine(root, ".codex/config.toml"))
}));
return 0;
