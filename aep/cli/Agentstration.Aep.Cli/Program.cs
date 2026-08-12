using System.Text.Json;
using Agentstration.Aep.Abstractions;
using Agentstration.Aep.Client;
using Agentstration.Aep.Validation;

if (args.Length < 2 || args[0] is not ("inspect" or "validate") || !Uri.TryCreate(args[1], UriKind.Absolute, out var endpoint))
{
    Console.Error.WriteLine("Usage: aep <inspect|validate> <http(s)://endpoint> [--format json]");
    return 2;
}
using var httpClient = new HttpClient { BaseAddress = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + '/'), Timeout = TimeSpan.FromSeconds(30) };
var client = new AepClient(httpClient);
try
{
    if (args[0] == "inspect")
    {
        var manifest = await client.GetManifestAsync();
        Console.WriteLine(JsonSerializer.Serialize(manifest, AepProtocol.JsonOptions));
        return 0;
    }
    var result = await new AepValidator().ValidateAsync(client);
    if (args.Contains("json", StringComparer.OrdinalIgnoreCase)) Console.WriteLine(JsonSerializer.Serialize(result, AepProtocol.JsonOptions));
    else
    {
        Console.WriteLine(result.IsValid ? "AEP extension is conformant." : "AEP extension is not conformant.");
        foreach (var issue in result.Issues) Console.WriteLine($"{issue.Code} {issue.Severity}: {issue.Message}");
    }
    return result.IsValid ? 0 : 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
