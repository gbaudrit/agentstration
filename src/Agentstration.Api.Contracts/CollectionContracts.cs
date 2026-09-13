namespace Agentstration.Api.Contracts;

public sealed record PagedResponse<T>(IReadOnlyList<T> Value, string? NextLink);
public sealed record ValueResponse<T>(IReadOnlyList<T> Value);
