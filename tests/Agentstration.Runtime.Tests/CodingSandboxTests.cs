extern alias codingworker;
using System.Diagnostics;
using System.Text.Json;
using Agentstration.Coding;
using Agentstration.Coding.Contracts;
using Agentstration.Resources;
using Agentstration.Runtime.Worker.Core;
using Agentstration.Work;
using Microsoft.Extensions.Options;
using Native = codingworker::Agentstration.Runtime.Worker.Coding;

namespace Agentstration.Runtime.Tests;

[TestClass]
public sealed class CodingSandboxTests
{
    [TestMethod]
    [DataRow("success", CodingConsumptionAvailability.Known, null)]
    [DataRow("partial", CodingConsumptionAvailability.Partial, null)]
    [DataRow("no-usage", CodingConsumptionAvailability.Unavailable, null)]
    [DataRow("fail", CodingConsumptionAvailability.Known, "coding_cli_failed")]
    public async Task IsolatedHelperChecksPinnedCheckoutProtectsAuthorityAndRetainsNativeEvidence(string mode, CodingConsumptionAvailability availability, string? diagnostic)
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("The optional native Coding worker requires Linux Bubblewrap.");
        await using var fixture = await Fixture.CreateAsync(mode);
        var records = new List<JsonElement>();
        var sandbox = new Native.CodingSandbox(Options.Create(fixture.Options), Options.Create(new RuntimeWorkerOptions { SharedKeyFile = fixture.Credential }));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var process = await sandbox.ExecuteAsync(fixture.Material, (line, token) =>
        {
            using var value = JsonDocument.Parse(line); records.Add(value.RootElement.Clone()); return Task.CompletedTask;
        }, timeout.Token);
        Assert.AreEqual(0, process.ExitCode, "The native helper process failed to start; verify Bubblewrap user namespaces are available.");
        var observation = records.Single(record => record.GetProperty("kind").GetString() == "consumption").GetProperty("value")
            .Deserialize<CodingConsumptionObservation>(Json)!;
        Assert.AreEqual(availability, observation.Availability);
        Assert.AreEqual(fixture.Material.Coordinates, observation.Coordinates);
        Assert.AreEqual(mode == "no-usage" ? null : (long?)13, observation.InputTokens);
        var result = records.Single(record => record.GetProperty("kind").GetString() == "result").GetProperty("value").Deserialize<CodingNativeResult>(Json)!;
        Assert.AreEqual(diagnostic, result.Diagnostic); StringAssert.Contains(result.Patch!, "answer.txt");
        Assert.AreEqual(mode == "fail" ? 17 : 0, result.CliExitCode);
        Assert.AreEqual(fixture.Material.Input.BaseRevision, result.BaseRevision);
        Assert.AreEqual(fixture.Material.Coordinates, result.Coordinates);
        if (mode != "fail") { Assert.AreEqual(0, result.Checks.Single().ExitCode); Assert.IsTrue(result.Checks.Single().Completed); }
        Assert.IsFalse(process.Output.Contains(Fixture.CredentialValue, StringComparison.Ordinal));
        Assert.AreEqual(Fixture.CredentialValue, await File.ReadAllTextAsync(fixture.Credential));
        Assert.AreEqual(fixture.Material.Input.BaseRevision, (await fixture.GitAsync(fixture.Bare, "rev-parse", "main")).Trim());
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Repository, "answer.txt")));
    }

    [TestMethod]
    [DataRow("{\"input_tokens\":0,\"cached_input_tokens\":0,\"output_tokens\":0}", CodingConsumptionAvailability.Known)]
    [DataRow("{\"input_tokens\":-1,\"output_tokens\":5}", CodingConsumptionAvailability.Partial)]
    [DataRow("{\"input_tokens\":1000000000001}", CodingConsumptionAvailability.Unavailable)]
    [DataRow("{\"input_tokens\":2,\"cached_input_tokens\":3}", CodingConsumptionAvailability.Unavailable)]
    [DataRow("{\"output_tokens\":2,\"reasoning_output_tokens\":3}", CodingConsumptionAvailability.Unavailable)]
    [DataRow("{}", CodingConsumptionAvailability.Unavailable)]
    public async Task NativeUsageValidatesCountersAndUsesStableInvocationCoordinates(string usage, CodingConsumptionAvailability availability)
    {
        await using var fixture = await Fixture.CreateAsync("success");
        var coordinates = fixture.Material.Coordinates;
        var line = "{\"type\":\"turn.completed\",\"usage\":" + usage + "}";
        var reader = new Native.CodingUsageReader(coordinates, TimeProvider.System);
        var first = reader.Read(line)!; Assert.AreEqual(availability, first.Availability);
        Assert.AreEqual(first.ObservationId, new Native.CodingUsageReader(coordinates, TimeProvider.System).Read(line)!.ObservationId);
        Assert.AreNotEqual(first.ObservationId, reader.Read(line)!.ObservationId);
        Assert.AreEqual(coordinates, first.Coordinates);
        var malformed = await Assert.ThrowsAsync<Native.CodingProcessException>(() => Task.Run(() => reader.Read("invalid")));
        Assert.AreEqual("coding_cli_event_invalid", malformed.Code);
    }

    [TestMethod]
    public async Task TimeoutRetainsPartialUsageAndNativeCredentialOutputIsRefused()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("The optional native Coding worker requires Linux Bubblewrap.");
        await using (var fixture = await Fixture.CreateAsync("cancel"))
        {
            fixture.Material = fixture.Material with { Configuration = fixture.Material.Configuration with { TimeoutSeconds = 3 } };
            var result = await fixture.RunAsync();
            Assert.AreEqual("coding_execution_timed_out", result.Diagnostic);
            Assert.AreEqual(CodingConsumptionAvailability.Partial, result.Consumption.Single().Availability);
            Assert.AreEqual((long?)13, result.Consumption.Single().InputTokens);
        }
        await using (var fixture = await Fixture.CreateAsync("secret"))
        {
            // Public disposable fixture, never a real account credential.
            const string key = "public-disposable-native-api-key-fixture";
            var account = Path.Combine(fixture.Root, "native-auth.json");
            await File.WriteAllTextAsync(account, JsonSerializer.Serialize(new { OPENAI_API_KEY = key }));
            fixture.Options.NativeApiKeyAuthFile = account;
            var result = await fixture.RunAsync();
            Assert.AreEqual("coding_native_credential_output_refused", result.Diagnostic);
            Assert.IsNull(result.Patch); Assert.AreEqual("", result.Summary);
            Assert.DoesNotContain(key, JsonSerializer.Serialize(result));
        }
    }

    [TestMethod]
    public async Task FailedRepositoryChecksAndMalformedCliEventsNeverReportSuccess()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("The optional native Coding worker requires Linux Bubblewrap.");
        await using var fixture = await Fixture.CreateAsync("success");
        var check = fixture.Material.Configuration.Checks.Single();
        fixture.Material = fixture.Material with { Configuration = fixture.Material.Configuration with { Checks = [check with { Arguments = [check.Arguments[0], "check", "fail"] }] } };
        var result = await fixture.RunAsync();
        Assert.AreEqual("coding_checks_failed", result.Diagnostic); Assert.AreEqual(9, result.Checks.Single().ExitCode);
        fixture.Options.CliPrefixArguments = fixture.Options.CliPrefixArguments.Select(value => value == "success" ? "flood" : value).ToArray();
        result = await fixture.RunAsync(); Assert.AreEqual("coding_cli_event_invalid", result.Diagnostic);
        Assert.AreEqual(CodingConsumptionAvailability.Unavailable, result.Consumption.Single().Availability);
    }

    [TestMethod]
    public async Task WrongCacheIdentityAndUnownedNativeFilesFailBeforeCliWrites()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("The optional native Coding worker requires Linux Bubblewrap.");
        await using var fixture = await Fixture.CreateAsync("success");
        await fixture.GitAsync(fixture.Bare, "config", "remote.origin.url", "https://github.com/foreign/fixture.git");
        var result = await fixture.RunAsync(); Assert.AreEqual("coding_checkout_identity_mismatch", result.Diagnostic);
        Assert.IsNull(result.Patch); Assert.IsNull(result.CliExitCode);
        await fixture.GitAsync(fixture.Bare, "config", "remote.origin.url", fixture.Material.Repository.CloneUrl.AbsoluteUri);
        await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "AGENTS.md"), "Unowned instructions\n");
        await fixture.GitAsync(fixture.Repository, "add", "AGENTS.md"); await fixture.GitAsync(fixture.Repository, "commit", "-m", "Unowned native instructions");
        await fixture.GitAsync(fixture.Repository, "push", fixture.Bare, "HEAD:refs/heads/main");
        fixture.Material = fixture.Material with { Input = fixture.Material.Input with { BaseRevision = (await fixture.GitAsync(fixture.Repository, "rev-parse", "HEAD")).Trim() } };
        result = await fixture.RunAsync(); Assert.AreEqual("harness_file_ownership_conflict", result.Diagnostic); Assert.IsNull(result.CliExitCode);
    }

    [TestMethod]
    public async Task CredentialMountOverlapIsRejectedBeforeStartingAProcess()
    {
        await using var fixture = await Fixture.CreateAsync("success");
        fixture.Options.ReadOnlyRuntimeDirectories = [fixture.Root];
        var sandbox = new Native.CodingSandbox(Options.Create(fixture.Options), Options.Create(new RuntimeWorkerOptions { SharedKeyFile = fixture.Credential }));
        var error = await Assert.ThrowsAsync<Native.CodingProcessException>(() => sandbox.ExecuteAsync(fixture.Material, (_, _) => Task.CompletedTask, default));
        Assert.AreEqual("coding_sandbox_credential_overlap", error.Code);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed class Fixture : IAsyncDisposable
    {
        internal const string CredentialValue = "public-disposable-coding-authority-fixture";
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "agentstration-native-coding-" + Guid.NewGuid().ToString("N"));
        internal string Repository => Path.Combine(Root, "repository");
        internal string Bare => Path.Combine(Root, "cache.git");
        internal string Credential => Path.Combine(Root, "authority-key");
        internal Native.CodingWorkerOptions Options { get; private set; } = null!;
        internal CodingExecutionMaterial Material { get; set; } = null!;
        internal static async Task<Fixture> CreateAsync(string mode)
        {
            var fixture = new Fixture(); Directory.CreateDirectory(fixture.Repository);
            await File.WriteAllTextAsync(fixture.Credential, CredentialValue);
            await fixture.GitAsync(fixture.Repository, "init", "--initial-branch=main");
            await fixture.GitAsync(fixture.Repository, "config", "user.name", "Native fixture");
            await fixture.GitAsync(fixture.Repository, "config", "user.email", "fixture@agentstration.invalid");
            await fixture.GitAsync(fixture.Repository, "config", "commit.gpgsign", "false");
            await File.WriteAllTextAsync(Path.Combine(fixture.Repository, "README.md"), "Native fixture\n");
            await fixture.GitAsync(fixture.Repository, "add", "."); await fixture.GitAsync(fixture.Repository, "commit", "-m", "Fixture base");
            await fixture.GitAsync(fixture.Root, "clone", "--bare", fixture.Repository, fixture.Bare);
            const string origin = "https://github.com/example/fixture.git";
            await fixture.GitAsync(fixture.Bare, "config", "remote.origin.url", origin);
            var root = FindRoot(); var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var fake = Path.Combine(root, "tests/fixtures/NativeCodingCli/bin", configuration, "net10.0/NativeCodingCli.dll");
            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "../../../dotnet"));
            fixture.Options = new()
            {
                CliExecutable = dotnet,
                CliPrefixArguments = [fake, "run-mode", mode, fixture.Credential],
                ReadOnlyRuntimeDirectories = [Path.GetDirectoryName(fake)!],
                CheckExecutables = new Dictionary<string, string> { ["fixture-check"] = dotnet },
                Repositories = [new("github", "42", origin, fixture.Bare)]
            };
            var workspace = WorkspaceId.New(); var scope = ResourceScopeRef.Workspace(workspace.Value);
            var native = new HarnessNativeConfigurationDefinition("codex", "1.0.0", new("fixture-model", "workspace-write"));
            var pin = new HarnessNativeConfigurationPin(scope, default, "native", "1.0.0", Guid.NewGuid(), 1, CodexHarnessMaterializer.ContentDigest(native), native);
            var definition = new HarnessProfileDefinition("Fixture", "Perform the requested bounded repository change.",
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { }, additionalProperties = false }), new("native", "1.0.0", default));
            var profile = new HarnessProfileVersionResource
            {
                ApiVersion = ResourceApiVersions.CoreV1,
                Kind = HarnessResourceKinds.ProfileVersion,
                ScopeRef = scope,
                Metadata = new() { Name = "fixture.v1.0.0" },
                Uid = Guid.NewGuid(),
                Generation = 1,
                ProfileName = "fixture",
                ProfileUid = Guid.NewGuid(),
                ProfileGeneration = 1,
                Version = "1.0.0",
                Definition = definition,
                NativeConfiguration = pin,
                ContentDigest = CodexHarnessMaterializer.ProfileDigest(definition, pin),
                PublishedAt = DateTimeOffset.UnixEpoch
            };
            var invocation = Guid.NewGuid();
            var coordinates = new CodingInvocationCoordinates(workspace, Guid.NewGuid(), new(Guid.NewGuid()), new(Guid.NewGuid()), 1, new string('a', 64),
                new(Guid.NewGuid()), 1, new string('b', 64), Guid.NewGuid(), WorkItemId.New(), "fixture-run", Guid.NewGuid(), invocation, invocation);
            var config = new CodingExecutionConfiguration(new("coding-local", default) { Uid = Guid.NewGuid(), Generation = 1 },
                new(profile.ProfileName, default, profile.Version, profile.ContentDigest) { Uid = profile.Uid, Generation = profile.Generation }, "example/fixture")
            { Checks = [new("check", "fixture-check", [fake, "check", "pass"])] };
            fixture.Material = new(CodingExecutionProtocol.MaterialVersion, coordinates, config, profile,
                new("github", config.Repository, "42", new(origin), new("https://github.com/example/fixture"), "main", new string('c', 40), []),
                new((await fixture.GitAsync(fixture.Repository, "rev-parse", "HEAD")).Trim(), new Dictionary<string, string>()), "Create the deterministic fixture answer.");
            Assert.IsTrue(CodingExecutionValidation.Material(fixture.Material)); return fixture;
        }
        internal async Task<CodingNativeResult> RunAsync()
        {
            CodingNativeResult? result = null;
            var sandbox = new Native.CodingSandbox(Microsoft.Extensions.Options.Options.Create(Options), Microsoft.Extensions.Options.Options.Create(new RuntimeWorkerOptions { SharedKeyFile = Credential }));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var process = await sandbox.ExecuteAsync(Material, (line, token) =>
            { using var document = JsonDocument.Parse(line); if (document.RootElement.GetProperty("kind").GetString() == "result") result = document.RootElement.GetProperty("value").Deserialize<CodingNativeResult>(Json); return Task.CompletedTask; }, timeout.Token);
            Assert.AreEqual(0, process.ExitCode); return result!;
        }
        internal async Task<string> GitAsync(string directory, params string[] arguments)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token); Assert.AreEqual(0, process.ExitCode, await error); return await output; }
            finally { if (!process.HasExited) process.Kill(true); }
        }
        private static string FindRoot()
        { for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent) if (File.Exists(Path.Combine(directory.FullName, "Agentstration.slnx"))) return directory.FullName; throw new InvalidOperationException("Fixture repository unavailable."); }
        public ValueTask DisposeAsync() { Directory.Delete(Root, true); return ValueTask.CompletedTask; }
    }
}
