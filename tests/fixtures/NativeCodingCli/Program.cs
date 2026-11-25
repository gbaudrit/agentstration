using System.Text.Json;

// Deterministic process fixture. No provider account, model call or network operation is performed.
if (args.Length > 0 && args[0] == "check")
{
    Console.WriteLine("Deterministic repository check.");
    return args.Length > 1 && args[1] == "fail" ? 9 : 0;
}
if (args.Length < 3 || args[0] != "run-mode") return 2;
var mode = args[1];
if (File.Exists(args[2])) return 91;
try { await File.AppendAllTextAsync("/work/.git/config", "\n# forbidden write\n"); return 92; }
catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { }
_ = await Console.In.ReadToEndAsync();
Console.WriteLine(JsonSerializer.Serialize(new { type = "turn.started" }));
if (mode == "flood")
{
    for (var index = 0; index < 1024; index++) Console.WriteLine(new string('a', 2048));
    return 0;
}
if (mode != "no-usage") Console.WriteLine(mode == "partial" || mode == "cancel"
    ? "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":13}}"
    : "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":13,\"cached_input_tokens\":3,\"output_tokens\":7,\"total_tokens\":23,\"reasoning_output_tokens\":2}}");
await Console.Out.FlushAsync();
if (mode == "cancel") { await Task.Delay(Timeout.InfiniteTimeSpan); return 0; }
await File.WriteAllTextAsync("/work/answer.txt", "42\n");
if (mode == "delete") File.Delete("/work/README.md");
if (mode == "utf8-bom") await File.WriteAllBytesAsync("/work/answer.txt", [0xef, 0xbb, 0xbf, 0x34, 0x32, 0x0a]);
if (mode == "executable" && OperatingSystem.IsLinux()) File.SetUnixFileMode("/work/answer.txt", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
if (mode == "symlink") { File.Delete("/work/answer.txt"); File.CreateSymbolicLink("/work/answer.txt", "README.md"); }
if (mode == "large-code") await File.WriteAllTextAsync("/work/answer.txt", new string('a', 25_000));
var last = Array.IndexOf(args, "--output-last-message");
if (last < 0 || last + 1 >= args.Length) return 3;
var summary = "Completed deterministic fixture change.";
if (mode == "secret")
{
    using var account = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Environment.GetEnvironmentVariable("HOME")!, ".codex/auth.json")));
    summary = account.RootElement.GetProperty("OPENAI_API_KEY").GetString()!;
    await File.WriteAllTextAsync("/work/answer.txt", summary);
}
await File.WriteAllTextAsync(args[last + 1], summary);
return mode == "fail" ? 17 : 0;
