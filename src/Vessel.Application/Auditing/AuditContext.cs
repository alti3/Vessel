using Vessel.Domain;
using Vessel.Domain.Auditing;

namespace Vessel.Application.Auditing;

/// <summary>
///     Where the current change comes from (IAM-08): the source channel, the trusted client address, a truncated user
///     agent and the API token that authenticated the request. Web supplies it per request; background work without a
///     request reports <see cref="AuditSources.System" /> unless a job sets its own scope.
/// </summary>
public sealed record AuditContext(
    string Source,
    string? IpAddress = null,
    string? UserAgent = null,
    PersonalAccessTokenId? TokenId = null)
{
    public static readonly AuditContext System = new(AuditSources.System);

    public static readonly AuditContext Job = new(AuditSources.Job);
}

public interface IAuditContextAccessor
{
    AuditContext Current { get; }
}

/// <summary>
///     Default accessor outside HTTP requests. An ambient <see cref="AuditContextScope" /> (for example around a
///     Hangfire job) wins over the default <see cref="AuditContext.System" />.
/// </summary>
public sealed class SystemAuditContextAccessor : IAuditContextAccessor
{
    public AuditContext Current => AuditContextScope.Ambient ?? AuditContext.System;
}

/// <summary>Async-local override of the audit context for background work.</summary>
public static class AuditContextScope
{
    private static readonly AsyncLocal<AuditContext?> Value = new();

    public static AuditContext? Ambient => Value.Value;

    public static IDisposable Begin(AuditContext context)
    {
        AuditContext? previous = Value.Value;
        Value.Value = context;
        return new Restore(previous);
    }

    private sealed class Restore(AuditContext? previous) : IDisposable
    {
        public void Dispose()
        {
            Value.Value = previous;
        }
    }
}
