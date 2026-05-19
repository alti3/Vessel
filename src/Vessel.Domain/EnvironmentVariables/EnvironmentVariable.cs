using Vessel.Domain.Common;

namespace Vessel.Domain.EnvironmentVariables;

public sealed class EnvironmentVariable : Entity<EnvironmentVariableId>
{
    private EnvironmentVariable()
    {
    }

    private EnvironmentVariable(
        EnvironmentVariableId id,
        TeamId teamId,
        EnvironmentVariableTargetType targetType,
        EnvironmentVariableKey key,
        EnvironmentVariableValueKind valueKind,
        string? plainValue,
        SecretReferenceId? secretReferenceId,
        bool isBuildTime,
        bool isRuntime,
        bool isPreview,
        bool isLiteral,
        bool isMultiline,
        string? comment,
        DateTimeOffset createdAt)
        : base(id, createdAt)
    {
        TeamId = teamId;
        TargetType = targetType;
        Key = key;
        ValueKind = valueKind;
        PlainValue = plainValue;
        SecretReferenceId = secretReferenceId;
        IsBuildTime = isBuildTime;
        IsRuntime = isRuntime;
        IsPreview = isPreview;
        IsLiteral = isLiteral;
        IsMultiline = isMultiline;
        Comment = DomainValidation.Optional(comment, nameof(Comment), 1000);
    }

    public TeamId TeamId { get; private set; }

    public ProjectId? ProjectId { get; private set; }

    public EnvironmentId? EnvironmentId { get; private set; }

    public ServerId? ServerId { get; private set; }

    public ApplicationId? ApplicationId { get; private set; }

    public DatabaseResourceId? DatabaseResourceId { get; private set; }

    public EnvironmentVariableTargetType TargetType { get; private set; }

    public EnvironmentVariableKey Key { get; private set; }

    public EnvironmentVariableValueKind ValueKind { get; private set; }

    public string? PlainValue { get; private set; }

    public SecretReferenceId? SecretReferenceId { get; private set; }

    public bool IsBuildTime { get; private set; }

    public bool IsRuntime { get; private set; }

    public bool IsPreview { get; private set; }

    public bool IsLiteral { get; private set; }

    public bool IsMultiline { get; private set; }

    public string? Comment { get; private set; }

    public static EnvironmentVariable Create(
        TeamId teamId,
        EnvironmentVariableTargetType targetType,
        EnvironmentVariableKey key,
        EnvironmentVariableValueKind valueKind,
        string? plainValue,
        SecretReferenceId? secretReferenceId,
        bool isBuildTime,
        bool isRuntime,
        bool isPreview,
        bool isLiteral,
        bool isMultiline,
        string? comment,
        DateTimeOffset now)
    {
        ValidateValue(valueKind, plainValue, secretReferenceId);

        return new EnvironmentVariable(EnvironmentVariableId.New(), teamId, targetType, key, valueKind,
            NormalizePlainValue(plainValue), secretReferenceId, isBuildTime, isRuntime, isPreview, isLiteral,
            isMultiline, comment, now);
    }

    public void TargetProject(ProjectId projectId)
    {
        ProjectId = projectId;
    }

    public void TargetEnvironment(ProjectId projectId, EnvironmentId environmentId)
    {
        ProjectId = projectId;
        EnvironmentId = environmentId;
    }

    public void TargetServer(ServerId serverId)
    {
        ServerId = serverId;
    }

    public void TargetApplication(ProjectId projectId, EnvironmentId environmentId, ApplicationId applicationId)
    {
        ProjectId = projectId;
        EnvironmentId = environmentId;
        ApplicationId = applicationId;
    }

    public void TargetDatabase(ProjectId projectId, EnvironmentId environmentId, DatabaseResourceId databaseResourceId)
    {
        ProjectId = projectId;
        EnvironmentId = environmentId;
        DatabaseResourceId = databaseResourceId;
    }

    public void Update(
        EnvironmentVariableValueKind valueKind,
        string? plainValue,
        SecretReferenceId? secretReferenceId,
        bool isBuildTime,
        bool isRuntime,
        bool isPreview,
        bool isLiteral,
        bool isMultiline,
        string? comment,
        DateTimeOffset now)
    {
        ValidateValue(valueKind, plainValue, secretReferenceId);
        ValueKind = valueKind;
        PlainValue = NormalizePlainValue(plainValue);
        SecretReferenceId = secretReferenceId;
        IsBuildTime = isBuildTime;
        IsRuntime = isRuntime;
        IsPreview = isPreview;
        IsLiteral = isLiteral;
        IsMultiline = isMultiline;
        Comment = DomainValidation.Optional(comment, nameof(Comment), 1000);
        Touch(now);
    }

    private static void ValidateValue(
        EnvironmentVariableValueKind valueKind,
        string? plainValue,
        SecretReferenceId? secretReferenceId)
    {
        if (valueKind == EnvironmentVariableValueKind.Secret && !secretReferenceId.HasValue)
            throw new DomainException("Secret environment variables require a secret reference.");

        if (valueKind != EnvironmentVariableValueKind.Secret && secretReferenceId.HasValue)
            throw new DomainException("Only secret environment variables can reference secret storage.");

        if (valueKind != EnvironmentVariableValueKind.Secret && plainValue is null)
            throw new DomainException("Plain and shared environment variables require a value.");
    }

    private static string? NormalizePlainValue(string? value)
    {
        return value is null ? null : DomainValidation.Required(value, nameof(PlainValue), 8000);
    }
}
