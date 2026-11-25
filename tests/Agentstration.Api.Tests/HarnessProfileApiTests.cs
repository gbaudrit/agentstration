using System.IO.Compression;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentstration.Coding;
using Agentstration.Coding.Bootstrap;
using Agentstration.Coding.Contracts;
using Agentstration.Identity.Contracts;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;
using Agentstration.Work;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentstration.Web.Tests;

[TestClass]
public sealed class HarnessProfileApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task FreshGitCloneRunsOfflineAfterAuthorityStopsAndRetainsV1AndNativeMergeConflicts()
    {
        await using var fixture = new Fixture(); await fixture.StartAsync();
        using var native = await fixture.Http.PostAsJsonAsync(fixture.Prefix + "/harness-configurations", Native());
        Assert.AreEqual(HttpStatusCode.Created, native.StatusCode);
        using var created = await fixture.Http.PostAsJsonAsync(fixture.Prefix + "/harness-profiles", Profile());
        var draft = (await created.Content.ReadFromJsonAsync<HarnessProfileResource>())!;
        using var published = await fixture.Http.PostAsJsonAsync(fixture.Prefix + "/namespaces/default/harness-profiles/coder/versions", new PublishHarnessProfileRequest("1.0.0", draft.ETag!));
        var v1 = (await published.Content.ReadFromJsonAsync<HarnessProfileVersionResource>())!;
        using var export = await fixture.Http.PostAsJsonAsync(fixture.Prefix + "/harness-export", Export(v1));
        Assert.AreEqual(HttpStatusCode.OK, export.StatusCode);
        var root = Path.Combine(Path.GetTempPath(), "agentstration-standalone-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        try
        {
            var repository = Path.Combine(root, "repository"); var clone = Path.Combine(root, "clone");
            System.IO.Directory.CreateDirectory(repository);
            using (var archive = new ZipArchive(new MemoryStream(await export.Content.ReadAsByteArrayAsync()))) archive.ExtractToDirectory(repository);
            await File.WriteAllTextAsync(Path.Combine(repository, ".gitattributes"), "* text eol=lf\n");
            var sourceRoot = RepositoryRoot();
            foreach (var relative in new[] { "AGENTS.md", ".codex/config.toml", "agentstration-harness.provenance.json" })
                CollectionAssert.AreEqual(await File.ReadAllBytesAsync(Path.Combine(sourceRoot, "tests/fixtures/standalone-codex", relative)),
                    await File.ReadAllBytesAsync(Path.Combine(repository, relative)), relative);
            await RunAsync(repository, "git", "init", "--initial-branch=main");
            await GitIdentityAsync(repository); await RunAsync(repository, "git", "add", ".");
            await RunAsync(repository, "git", "commit", "-m", "Native harness v1");
            var original = (await RunAsync(repository, "git", "rev-parse", "HEAD")).Trim();
            await RunAsync(root, "git", "-c", "core.autocrlf=false", "clone", "--no-local", repository, clone);
            var cloneBytes = await File.ReadAllBytesAsync(Path.Combine(clone, "AGENTS.md"));
            await fixture.StopAsync();
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var probe = Path.Combine(sourceRoot, "tests/fixtures/NativeHarnessProbe/bin", configuration, "net10.0/NativeHarnessProbe.dll");
            // No request context, server, SDK or resource store is present in this launched process.
            using var report = JsonDocument.Parse(await RunAsync(clone, DotnetHost(), probe, "--verify"));
            Assert.AreEqual("offline-inventory-probe", report.RootElement.GetProperty("mode").GetString());
            Assert.AreEqual("1.0.0", report.RootElement.GetProperty("version").GetString());
            Assert.AreEqual("Implement fixture-project.\n", report.RootElement.GetProperty("instructions").GetString());
            await fixture.StartAsync();
            using var update = await fixture.Http.PutAsJsonAsync(fixture.Prefix + "/namespaces/default/harness-profiles/coder",
                new HarnessProfileUpdateRequest(draft.Definition with { Instructions = "Deliver {{project-name}} v2." }, draft.ETag!));
            var changed = (await update.Content.ReadFromJsonAsync<HarnessProfileResource>())!;
            using var publishedV2 = await fixture.Http.PostAsJsonAsync(fixture.Prefix + "/namespaces/default/harness-profiles/coder/versions", new PublishHarnessProfileRequest("2.0.0", changed.ETag!));
            Assert.AreEqual(HttpStatusCode.Created, publishedV2.StatusCode);
            CollectionAssert.AreEqual(cloneBytes, await File.ReadAllBytesAsync(Path.Combine(clone, "AGENTS.md")));
            Assert.AreEqual(original, (await RunAsync(clone, "git", "rev-parse", "HEAD")).Trim());
            await fixture.StopAsync();
            await GitIdentityAsync(clone); await RunAsync(clone, "git", "checkout", "-b", "local-edit");
            await File.WriteAllTextAsync(Path.Combine(clone, "AGENTS.md"), "Local native instruction edit.\n");
            await RunAsync(clone, "git", "add", "AGENTS.md"); await RunAsync(clone, "git", "commit", "-m", "Local edit");
            await RunAsync(clone, "git", "checkout", "main");
            await File.WriteAllTextAsync(Path.Combine(clone, "AGENTS.md"), "Another native instruction edit.\n");
            await RunAsync(clone, "git", "add", "AGENTS.md"); await RunAsync(clone, "git", "commit", "-m", "Conflicting edit");
            await RunAsync(clone, "git", 1, "merge", "--no-edit", "local-edit");
            StringAssert.Contains(await RunAsync(clone, "git", "diff", "--name-only", "--diff-filter=U"), "AGENTS.md");
            StringAssert.Contains(await File.ReadAllTextAsync(Path.Combine(clone, "AGENTS.md")), "<<<<<<< HEAD");
            await RunAsync(clone, DotnetHost(), 6, probe, "--verify");
        }
        finally { System.IO.Directory.Delete(root, true); }
    }

    private static async Task GitIdentityAsync(string directory)
    {
        await RunAsync(directory, "git", "config", "user.name", "Standalone fixture");
        await RunAsync(directory, "git", "config", "user.email", "fixture@agentstration.invalid");
        await RunAsync(directory, "git", "config", "commit.gpgsign", "false");
        await RunAsync(directory, "git", "config", "core.autocrlf", "false");
    }
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Agentstration.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Repository fixture unavailable.");
    }
    private static string DotnetHost() => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
        ?? Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "../../../dotnet" + (OperatingSystem.IsWindows() ? ".exe" : ""));
    private static Task<string> RunAsync(string directory, string executable, params string[] arguments) => RunAsync(directory, executable, 0, arguments);
    private static async Task<string> RunAsync(string directory, string executable, int expectedExit, params string[] arguments)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(start)!;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Assert.AreEqual(expectedExit, process.ExitCode, await error); return await output;
        }
        finally { if (!process.HasExited) process.Kill(true); }
    }

    [TestMethod]
    public async Task PublishExportAndRestartKeepExactVersionsAndDeterministicOwnedNativeFiles()
    {
        await using var fixture = new Fixture(); await fixture.StartAsync();
        var prefix = fixture.Prefix;
        using var nativeResponse = await fixture.Http.PostAsJsonAsync(prefix + "/harness-configurations", Native());
        Assert.AreEqual(HttpStatusCode.Created, nativeResponse.StatusCode, await nativeResponse.Content.ReadAsStringAsync());
        var native = (await nativeResponse.Content.ReadFromJsonAsync<HarnessNativeConfigurationResource>())!;
        using var created = await fixture.Http.PostAsJsonAsync(prefix + "/harness-profiles", Profile());
        Assert.AreEqual(HttpStatusCode.Created, created.StatusCode, await created.Content.ReadAsStringAsync());
        var draft = (await created.Content.ReadFromJsonAsync<HarnessProfileResource>())!;
        using var published = await fixture.Http.PostAsJsonAsync(prefix + "/namespaces/default/harness-profiles/coder/versions", new PublishHarnessProfileRequest("1.0.0", draft.ETag!));
        Assert.AreEqual(HttpStatusCode.Created, published.StatusCode, await published.Content.ReadAsStringAsync());
        var version = (await published.Content.ReadFromJsonAsync<HarnessProfileVersionResource>())!;
        Assert.AreEqual(native.Uid, version.NativeConfiguration.Uid); Assert.AreEqual(native.ContentDigest, version.NativeConfiguration.ContentDigest);
        var command = Export(version);
        using var previewResponse = await fixture.Http.PostAsJsonAsync(prefix + "/harness-preview", command);
        Assert.AreEqual(HttpStatusCode.OK, previewResponse.StatusCode, await previewResponse.Content.ReadAsStringAsync());
        var preview = (await previewResponse.Content.ReadFromJsonAsync<HarnessExportPreview>())!;
        using var first = await fixture.Http.PostAsJsonAsync(prefix + "/harness-export", command);
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode, await first.Content.ReadAsStringAsync());
        var firstBytes = await first.Content.ReadAsByteArrayAsync();
        using (var archive = new ZipArchive(new MemoryStream(firstBytes)))
        {
            CollectionAssert.AreEqual(new[] { ".codex/config.toml", "AGENTS.md", "agentstration-harness.provenance.json" }, archive.Entries.Select(entry => entry.FullName).ToArray());
            foreach (var entry in archive.Entries) Assert.AreEqual(1980, entry.LastWriteTime.Year);
            using var reader = new StreamReader(archive.GetEntry("AGENTS.md")!.Open());
            Assert.AreEqual("Implement fixture-project.\n", await reader.ReadToEndAsync());
        }
        await fixture.RestartAsync();
        using var second = await fixture.Http.PostAsJsonAsync(fixture.Prefix + "/harness-export", command);
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode, await second.Content.ReadAsStringAsync());
        CollectionAssert.AreEqual(firstBytes, await second.Content.ReadAsByteArrayAsync());
        Assert.AreEqual(first.Headers.ETag, second.Headers.ETag);
        var owned = command with { ExistingFiles = preview.Files.Select(file => new HarnessExistingFile(file.Path, file.Digest, file.OwnerProfile)).ToArray() };
        using var replay = await fixture.Http.PostAsJsonAsync(fixture.Prefix + "/harness-preview", owned);
        Assert.AreEqual(HttpStatusCode.OK, replay.StatusCode, await replay.Content.ReadAsStringAsync());
        Assert.AreEqual(preview.Digest, (await replay.Content.ReadFromJsonAsync<HarnessExportPreview>())!.Digest);
        using var update = await fixture.Http.PutAsJsonAsync(fixture.Prefix + "/namespaces/default/harness-profiles/coder",
            new HarnessProfileUpdateRequest(draft.Definition with { Instructions = "Operator-edited draft" }, draft.ETag!));
        Assert.AreEqual(HttpStatusCode.OK, update.StatusCode, await update.Content.ReadAsStringAsync());
        using var immutableConflict = await fixture.Http.PostAsJsonAsync(fixture.Prefix + "/namespaces/default/harness-profiles/coder/versions",
            new PublishHarnessProfileRequest("1.0.0", ((await update.Content.ReadFromJsonAsync<HarnessProfileResource>())!).ETag!));
        Assert.AreEqual(HttpStatusCode.Conflict, immutableConflict.StatusCode);
        using var oldExport = await fixture.Http.PostAsJsonAsync(fixture.Prefix + "/harness-export", command);
        CollectionAssert.AreEqual(firstBytes, await oldExport.Content.ReadAsByteArrayAsync());
    }

    [TestMethod]
    public async Task ProfileValidationStaleWritesOwnershipAndForeignReferencesFailBeforeNativeEffects()
    {
        await using var fixture = new Fixture(); await fixture.StartAsync();
        using var scope = fixture.Scope(); var service = fixture.Host.Services.GetRequiredService<HarnessProfileService>();
        var workspace = new WorkspaceId(fixture.Context.WorkspaceId);
        Assert.IsFalse((await service.CreateAsync(workspace, Profile(), default)).IsSuccess);
        Assert.IsTrue((await service.CreateConfigurationAsync(workspace, Native(), default)).IsSuccess);
        var profile = Profile();
        foreach (var definition in new[]
        {
            profile.Definition with { InstructionsPath = "../AGENTS.md" },
            profile.Definition with { Instructions = "{{missing-binding}}" },
            profile.Definition with { BindingsSchema = JsonSerializer.SerializeToElement(new { type = "array" }) },
            profile.Definition with { NativeConfiguration = profile.Definition.NativeConfiguration with { ScopeRef = ResourceScopeRef.Workspace(Guid.NewGuid()) } }
        }) Assert.IsFalse((await service.CreateAsync(workspace, profile with { Definition = definition }, default)).IsSuccess);
        var created = await service.CreateAsync(workspace, profile, default); Assert.IsTrue(created.IsSuccess, created.ErrorCode);
        Assert.AreEqual("harness_profile_conflict", (await service.UpdateAsync(workspace, default, "coder", profile.Definition, "stale", default)).ErrorCode);
        var published = await service.PublishAsync(workspace, default, "coder", new("1.0.0", created.Value!.ETag), default);
        Assert.IsTrue(published.IsSuccess, published.ErrorCode);
        var replay = await service.PublishAsync(workspace, default, "coder", new("1.0.0", created.Value.ETag), default);
        Assert.AreEqual(published.Value!.Value.Uid, replay.Value!.Value.Uid);
        var command = Export(published.Value.Value);
        Assert.IsFalse((await service.PreviewAsync(workspace, command with { Bindings = new Dictionary<string, string>() }, default)).IsSuccess);
        Assert.IsFalse((await service.PreviewAsync(workspace, command with { ExpectedDigest = new string('a', 64) }, default)).IsSuccess);
        Assert.IsFalse((await service.PreviewAsync(workspace, command with { ExistingFiles = [new("AGENTS.md", new string('a', 64), null)] }, default)).IsSuccess);
        Assert.IsFalse((await service.PreviewAsync(new(Guid.NewGuid()), command, default)).IsSuccess);
        using (fixture.Host.Services.GetRequiredService<IRequestContextScopeFactory>().Push(fixture.Context with { PrincipalId = Guid.NewGuid() }))
            Assert.IsFalse((await service.PreviewAsync(workspace, command, default)).IsSuccess);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Host.Services.GetRequiredService<IResourceStore>().PutExactAsync(
            ResourceScopeRef.Workspace(workspace.Value), published.Value.Value, published.Value.ETag, false, default));
    }

    [TestMethod]
    public async Task ManagedProfilePublicationRequiresItsExactAuthorityAndKeepsNativeConfigurationPinned()
    {
        await using var fixture = new Fixture(); await fixture.StartAsync(); using var scope = fixture.Scope();
        var service = fixture.Host.Services.GetRequiredService<HarnessProfileService>(); var workspace = new WorkspaceId(fixture.Context.WorkspaceId);
        Assert.IsTrue((await service.CreateConfigurationAsync(workspace, Native(), default)).IsSuccess);
        var governance = fixture.Host.Services.GetRequiredService<IResourceGovernanceContext>();
        Agentstration.ResourceManagement.StoredResource<HarnessProfileResource> draft;
        using (governance.Push(ResourceGovernance.Core("harness-fixture")))
        {
            var result = await service.CreateAsync(workspace, Profile(), default); Assert.IsTrue(result.IsSuccess, result.ErrorCode); draft = result.Value!;
        }
        var denied = await service.PublishAsync(workspace, default, "coder", new("1.0.0", draft.ETag), default);
        Assert.AreEqual("resource_managed", denied.ErrorCode);
        using (governance.Push(ResourceGovernance.Core("other-authority")))
            Assert.AreEqual("resource_managed", (await service.PublishAsync(workspace, default, "coder", new("1.0.0", draft.ETag), default)).ErrorCode);
        using (governance.Push(ResourceGovernance.Core("harness-fixture")))
        {
            var published = await service.PublishAsync(workspace, default, "coder", new("1.0.0", draft.ETag), default);
            Assert.IsTrue(published.IsSuccess, published.ErrorCode); Assert.AreEqual(draft.Value.Governance, published.Value!.Value.Governance);
        }
        var changedNative = Native() with { Definition = Native().Definition with { Settings = new("different-model") } };
        Assert.AreEqual("harness_configuration_version_conflict", (await service.CreateConfigurationAsync(workspace, changedNative, default)).ErrorCode);
    }

    [TestMethod]
    public async Task WorkspaceBootstrapPlansExactNativeReferencesAndPreservesEditedDraftsOnReplay()
    {
        await using var fixture = new Fixture(); await fixture.StartAsync(); using var scope = fixture.Scope();
        using var services = fixture.Host.Services.CreateScope();
        var native = services.ServiceProvider.GetServices<IBootstrapResourceHandler>().Single(handler => handler.Kind == HarnessResourceKinds.NativeConfiguration);
        var profile = services.ServiceProvider.GetServices<IBootstrapResourceHandler>().Single(handler => handler.Kind == HarnessResourceKinds.Profile);
        var operation = new BootstrapResourceOperationContext("harness-fixture", fixture.Directory, BootstrapProfileScope.Workspace,
            new(fixture.Context.TenantId, fixture.Context.WorkspaceId)) { ActorPrincipalId = fixture.Context.PrincipalId };
        var nativeDoc = new BootstrapResourceDocument { ApiVersion = ResourceApiVersions.CoreV1, Kind = native.Kind, Metadata = new() { Name = "native" },
            Definition = JsonSerializer.SerializeToElement(new DeclarativeHarnessNativeConfiguration("1.0.0", "codex", "1.0.0", new("fixture-model")), Json) };
        var profileDoc = new BootstrapResourceDocument { ApiVersion = ResourceApiVersions.CoreV1, Kind = profile.Kind, Metadata = new() { Name = "coder" },
            Definition = JsonSerializer.SerializeToElement(new DeclarativeHarnessProfile("1.0.0", Profile().Definition), Json) };
        Assert.AreEqual(BootstrapResourceDisposition.Invalid, (await profile.PlanAsync(profileDoc, operation, new(), default)).Disposition);
        var planning = new BootstrapPlanningContext();
        Assert.AreEqual(BootstrapResourceDisposition.Create, (await native.PlanAsync(nativeDoc, operation, planning, default)).Disposition);
        Assert.AreEqual(BootstrapResourceDisposition.Create, (await profile.PlanAsync(profileDoc, operation, planning, default)).Disposition);
        Assert.AreEqual(BootstrapResourceApplyResult.Created, await native.ApplyAsync(nativeDoc, operation, default));
        Assert.AreEqual(BootstrapResourceApplyResult.Created, await profile.ApplyAsync(profileDoc, operation, default));
        Assert.AreEqual(BootstrapResourceApplyResult.Skipped, await native.ApplyAsync(nativeDoc, operation, default));
        var service = fixture.Host.Services.GetRequiredService<HarnessProfileService>(); var workspace = new WorkspaceId(fixture.Context.WorkspaceId);
        var draft = (await service.GetAsync(workspace, default, "coder", default)).Value!;
        Assert.IsTrue((await service.UpdateAsync(workspace, default, "coder", draft.Value.Definition with { Instructions = "Locally edited" }, draft.ETag, default)).IsSuccess);
        Assert.AreEqual(BootstrapResourceApplyResult.Skipped, await profile.ApplyAsync(profileDoc, operation, default));
        Assert.AreEqual("Locally edited", (await service.GetAsync(workspace, default, "coder", default)).Value!.Value.Definition.Instructions);
        Assert.AreEqual(BootstrapResourceApplyResult.Conflict, await profile.ApplyAsync(profileDoc, operation with { ProfileScope = BootstrapProfileScope.Instance }, default));
    }

    private static CreateHarnessNativeConfigurationRequest Native() => new("native", default, "1.0.0", new("codex", "1.0.0", new("fixture-model")));
    private static HarnessProfileResource Profile() => new()
    {
        ApiVersion = ResourceApiVersions.CoreV1, Kind = HarnessResourceKinds.Profile, Metadata = new() { Name = "coder" },
        Definition = new("Coder", "Implement {{project-name}}.", JsonSerializer.SerializeToElement(new { type = "object",
            properties = new Dictionary<string, object> { ["project-name"] = new { type = "string", maxLength = 64 } }, required = new[] { "project-name" }, additionalProperties = false }), new("native", "1.0.0", default))
    };
    private static HarnessExportRequest Export(HarnessProfileVersionResource profile) => new(default, profile.ProfileName, profile.Version, profile.ContentDigest,
        new Dictionary<string, string> { ["project-name"] = "fixture-project" }, []);
    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "agentstration-harness-" + Guid.NewGuid().ToString("N"));
        public WebApplicationFactory<global::Program> Host { get; private set; } = null!;
        public HttpClient Http { get; private set; } = null!;
        public RequestContext Context { get; private set; } = null!;
        public string Prefix => $"/api/workspaces/{Context.WorkspaceId}/coding";
        public async Task StartAsync()
        {
            stopped = false;
            Host = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development"); builder.UseSetting("Data:Directory", Directory);
                builder.UseSetting("Agentstration:Authentication:Mode", "Development"); builder.UseSetting("AI:Provider", "Deterministic");
                builder.UseSetting("Agentstration:Bootstrap:InitialBootstrapEnabled", "false"); builder.UseSetting("Logging:LogLevel:Default", "Warning");
            });
            Http = Host.CreateClient(); Context = await Host.Services.GetRequiredService<ILocalEnvironmentBootstrapper>().EnsureInitializedAsync(default);
        }
        public IDisposable Scope() => Host.Services.GetRequiredService<IRequestContextScopeFactory>().Push(Context);
        private bool stopped;
        public async Task StopAsync() { if (stopped) return; stopped = true; Http.Dispose(); await Host.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }
        public async Task RestartAsync() { await StopAsync(); await StartAsync(); }
        public async ValueTask DisposeAsync() { await StopAsync(); if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
    }
}
