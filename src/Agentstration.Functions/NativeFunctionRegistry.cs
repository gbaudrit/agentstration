using Agentstration.Functions.Abstractions;

namespace Agentstration.Functions;

public sealed class NativeFunctionRegistry
{
    private readonly IReadOnlyDictionary<(string Id, string Version), INativeTransformationFunction> functions;
    public NativeFunctionRegistry(IEnumerable<INativeTransformationFunction> functions)
    {
        var registered = new Dictionary<(string, string), INativeTransformationFunction>();
        foreach (var function in functions)
        {
            if (string.IsNullOrWhiteSpace(function.Descriptor.Id) || function.Descriptor.Id.Length > 128
                || string.IsNullOrWhiteSpace(function.Descriptor.Version) || function.Descriptor.Version.Length > 64)
                throw new TransformationFunctionException("function_implementation_invalid", "Native implementations require bounded stable identities and versions.");
            _ = Agentstration.Schemas.DataContracts.Declared(function.Descriptor.InputSchema, function.Descriptor.Id + "/input");
            _ = Agentstration.Schemas.DataContracts.Declared(function.Descriptor.OutputSchema, function.Descriptor.Id + "/output");
            if (!registered.TryAdd((function.Descriptor.Id, function.Descriptor.Version), function))
                throw new TransformationFunctionException("function_implementation_duplicate", "A native implementation identity and version must be unique.");
        }
        this.functions = registered;
    }

    public IReadOnlyList<NativeFunctionDescriptor> List() => functions.Values.Select(f => f.Descriptor).OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
    public INativeTransformationFunction Resolve(string id, string version) => functions.GetValueOrDefault((id, version))
        ?? throw new TransformationFunctionException("function_implementation_unavailable", "The exact native implementation is unavailable.");
}
