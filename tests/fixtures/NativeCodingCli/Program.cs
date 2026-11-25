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
    : "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":13,\"cached_input_tokens\":3,\"output_tokens\":7,\"reasoning_output_tokens\":2}}");
await Console.Out.FlushAsync();
if (mode == "cancel") { await Task.Delay(Timeout.InfiniteTimeSpan); return 0; }
await File.WriteAllTextAsync("/work/answer.txt", "42\n");
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
