using Vessel.Domain.Common;

namespace Vessel.Domain.Settings;

public sealed class SettingEntry : Entity<SettingId>
{
    private SettingEntry()
    {
    }

    private SettingEntry(
        SettingId id,
        SettingScope scope,
        TeamId? teamId,
        ProjectId? projectId,
        string? resourceType,
        string? resourceId,
        string key,
        string value,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        Scope = scope;
        TeamId = teamId;
        ProjectId = projectId;
        ResourceType = resourceType;
        ResourceId = resourceId;
        Key = key;
        Value = value;
    }

    public SettingScope Scope { get; private set; }

    public TeamId? TeamId { get; private set; }

    public ProjectId? ProjectId { get; private set; }

    public string? ResourceType { get; private set; }

    public string? ResourceId { get; private set; }

    public string Key { get; private set; } = string.Empty;

    public string Value { get; private set; } = string.Empty;

    public static SettingEntry Create(
        SettingScope scope,
        string key,
        string value,
        DateTimeOffset now,
        TeamId? teamId = null,
        ProjectId? projectId = null,
        string? resourceType = null,
        string? resourceId = null)
    {
        return new SettingEntry(
            SettingId.New(),
            scope,
            teamId,
            projectId,
            DomainValidation.Optional(resourceType, nameof(ResourceType), 120),
            DomainValidation.Optional(resourceId, nameof(ResourceId), 160),
            DomainValidation.Required(key, nameof(Key), 160),
            DomainValidation.Required(value, nameof(Value), 4000),
            now);
    }

    public void UpdateValue(string value, DateTimeOffset now)
    {
        Value = DomainValidation.Required(value, nameof(Value), 4000);
        Touch(now);
    }
}
