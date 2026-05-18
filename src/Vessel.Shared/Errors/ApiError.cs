namespace Vessel.Shared.Errors;

public sealed record ApiError(
    string Code,
    string Message,
    IReadOnlyDictionary<string, object?> Details);
