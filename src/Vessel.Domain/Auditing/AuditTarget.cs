using Vessel.Domain.Common;

namespace Vessel.Domain.Auditing;

public readonly record struct AuditTarget
{
    public AuditTarget(string type, string id)
    {
        Type = DomainValidation.Required(type, nameof(Type), 120);
        Id = DomainValidation.Required(id, nameof(Id), 160);
    }

    public string Type { get; }

    public string Id { get; }
}
