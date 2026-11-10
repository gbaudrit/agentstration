using System.Text.RegularExpressions;

namespace Agentstration.ArchitectureTests;

[TestClass]
public sealed partial class SharedFormAdoptionTests
{
    // Collection-only filters and navigation belong to their separate contracts
    // (#1121). Native primitives implement the shared control itself.
    private static readonly HashSet<string> NativeControlOwners = new(StringComparer.Ordinal)
    {
        "src/Agentstration.Web.Components/ClearableText.razor",
        "src/Agentstration.Web.Components/ClearableNumber.razor",
        "src/Agentstration.Web.Components/ClearableSelect.razor",
        "src/Agentstration.Web.Components/MainLayout.razor",
        "src/Agentstration.Workplace.Web/Components/Pages/Home.razor",
        "src/Agentstration.Console.Components/Components/Pages/Agents.razor",
        "src/Agentstration.Console.Components/Components/Pages/ModelProfiles.razor",
        "src/Agentstration.Console.Components/Components/Pages/Tasks.razor",
        "src/Agentstration.Console.Components/Components/Pages/FlowRuns.razor",
        "src/Agentstration.Console.Components/Components/Pages/RunEvents.razor",
        "src/Agentstration.Console.Components/Components/Pages/Triggers.razor",
        "src/Agentstration.Console.Components/Components/Pages/ToolGovernanceAudit.razor",
        "src/Agentstration.Console.Components/Components/Pages/ResourceScopes.razor",
        "src/Agentstration.Console.Components/Components/Pages/Schemas.razor",
        "src/Agentstration.Console.Components/Components/Pages/Secrets.razor",
        "src/Agentstration.Console.Components/Components/Pages/Parameters.razor"
    };

    [TestMethod]
    public void OrdinaryFormControlsUseTheSharedInteractiveOrServerAdapter()
    {
        var root = RepositoryRoot();
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*", SearchOption.AllDirectories)
                     .Where(file => file.EndsWith(".razor", StringComparison.Ordinal) || file.EndsWith(".cshtml", StringComparison.Ordinal)))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)
                || NativeControlOwners.Contains(relative)) continue;
            foreach (Match control in NativeControls().Matches(File.ReadAllText(file)))
            {
                if (SpecializedNativeControl().IsMatch(control.Value)) continue;
                if (file.EndsWith(".cshtml", StringComparison.Ordinal)
                    && (control.Value.Contains("clear-label=", StringComparison.Ordinal)
                        || control.Value.Contains("asp-for=\"Input.RememberMe\"", StringComparison.Ordinal))) continue;
                violations.Add($"{relative}: {control.Value}");
            }
        }
        Assert.IsEmpty(violations, "Ordinary form fields must share labels, validation and in-field clearing.\n" + string.Join('\n', violations));
    }

    [GeneratedRegex(@"<(?:input|textarea|select|InputText(?:Area)?|InputNumber|InputSelect|InputCheckbox)\b[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex NativeControls();
    [GeneratedRegex("type=\"(?:hidden|checkbox|radio|file)\"", RegexOptions.CultureInvariant)]
    private static partial Regex SpecializedNativeControl();
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Agentstration.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Repository root was not found.");
    }
}
