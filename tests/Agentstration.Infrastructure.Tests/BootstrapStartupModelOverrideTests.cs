using Agentstration.Infrastructure.Bootstrap;
using Agentstration.Models;
using Agentstration.ResourceManagement;
using Agentstration.ResourceManagement.Contracts;
using Agentstration.Resources;

namespace Agentstration.Infrastructure.Tests;

[TestClass]
public sealed class BootstrapStartupModelOverrideTests
{
    [TestMethod]
    public async Task AcceptedChoiceIsPersistedWithoutProfileContentsAndReplayedOnlyForTheSameSourcePlan()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bootstrap-choice-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (state, operation, original) = Planned();
            using (var changes = new BootstrapStartupModelOverrideStore(new(directory), state, TimeProvider.System))
            {
                var entry = await changes.RegisterAsync(original, operation, ResourceScopeRef.Instance, default);
                await changes.SaveAsync(entry, new("replacement-provider", ResourceScopeRef.Instance), Guid.NewGuid(), "available:model", Guid.NewGuid(), default);
                Assert.IsFalse(await changes.TryCloseAsync(entry.AttemptId, entry.ResourceId, 0, default), "A correction racing model readiness must restart resolution.");
                var selected = await changes.GetAsync(entry.AttemptId, entry.ResourceId, default);
                Assert.AreEqual(1L, selected.Version);
                Assert.AreEqual("available:model", selected.Model);
                var json = await File.ReadAllTextAsync(Directory.GetFiles(directory, "*.json").Single());
                Assert.IsFalse(json.Contains("private-profile-text", StringComparison.Ordinal));
                Assert.IsTrue(await changes.TryCloseAsync(selected.AttemptId, selected.ResourceId, selected.Version, default));
                await Assert.ThrowsAsync<ResourceConcurrencyException>(() => changes.SaveAsync(selected,
                    selected.Provider, selected.ProviderUid!.Value, "another:model", Guid.NewGuid(), default));
            }
            var (restart, restartOperation, restartProfile) = Planned();
            using var recovered = new BootstrapStartupModelOverrideStore(new(directory), restart, TimeProvider.System);
            var replayed = await recovered.RegisterAsync(restartProfile, restartOperation, ResourceScopeRef.Instance, default);
            Assert.AreEqual("available:model", replayed.Model);
            Assert.AreEqual("replacement-provider", replayed.Provider.Name);
            Assert.IsNotNull(restart.ReadResources(new(restart.Read().AttemptId))!.Items.Single().Substitution);
            var changedPlan = await recovered.RegisterAsync(restartProfile,
                restartOperation with { StartupProfileDigest = "different-source" }, ResourceScopeRef.Instance, default);
            Assert.AreEqual("missing:model", changedPlan.Model);
            Assert.AreEqual(0L, changedPlan.Version);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task ConcurrentOperatorsCannotOverwriteEachOtherAndFailedModelCanRequestAnExplicitRetry()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bootstrap-choice-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (state, operation, profile) = Planned();
            using var changes = new BootstrapStartupModelOverrideStore(new(directory), state, TimeProvider.System);
            var entry = await changes.RegisterAsync(profile, operation, ResourceScopeRef.Instance, default);
            state.FinishResource(entry.ResourceId, "failed"); state.Fail("model_dependency_timeout");
            var resumed = changes.WaitForResumeAsync(entry.AttemptId, default);
            async Task<bool> SaveAsync(string model)
            {
                try { await changes.SaveAsync(entry, entry.Provider, Guid.NewGuid(), model, Guid.NewGuid(), default); return true; }
                catch (ResourceConcurrencyException) { return false; }
            }
            var results = await Task.WhenAll(SaveAsync("first:model"), SaveAsync("second:model"));
            Assert.AreEqual(1, results.Count(value => value));
            await resumed.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsTrue(await changes.HasCorrectableModelAsync(default));
            Assert.IsFalse(state.IsReady);
            var oldAttempt = entry.AttemptId;
            state.BeginRetry();
            Assert.IsTrue(state.AdministrationReady);
            await Assert.ThrowsAsync<ResourceConcurrencyException>(() => changes.GetAsync(oldAttempt, entry.ResourceId, default));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static (BootstrapStartupState State, BootstrapResourceOperationContext Operation, ModelProfileResource Profile) Planned()
    {
        var state = new BootstrapStartupState(); state.Begin(); state.EnableAdministration(); state.MoveTo("resources");
        var id = state.RegisterResource("/instance", "models", "profile.yaml", "resources", "ModelProfile", "chat");
        state.StartResource("/instance", "models", "profile.yaml", "ModelProfile", "chat", "default");
        var operation = new BootstrapResourceOperationContext("models", ".", BootstrapProfileScope.Instance, WaitForDependencies: true)
        { StartupResourceId = id, StartupProfileDigest = "source-v1" };
        var profile = new ModelProfileResource
        {
            ApiVersion = ResourceApiVersions.CoreV1,
            Kind = ModelResourceKinds.ModelProfile,
            Metadata = new() { Name = "chat" },
            ScopeRef = ResourceScopeRef.Instance,
            Definition = new()
            {
                DisplayName = "Chat",
                Provider = new("original-provider", ResourceScopeRef.Instance),
                Model = new() { Name = "missing:model" },
                Generation = new() { StopSequences = ["private-profile-text"] }
            }
        };
        return (state, operation, profile);
    }
}
