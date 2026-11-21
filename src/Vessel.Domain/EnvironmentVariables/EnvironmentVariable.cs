using Vessel.Domain.Common;

namespace Vessel.Domain.EnvironmentVariables;

/// <summary>
///     An environment variable of a team scope or resource (DEP-10). Plain values are stored exactly as entered: no
///     trimming, empty values allowed, trailing newlines of multiline values kept (upstream v4.5.0 #12024, v4.7.0 #9438).
///     <see cref="UsesLegacyEscaping" /> marks variables created before exact values; they keep the old env-file quoting
///     until they are saved again (upstream <c>uses_legacy_escaping</c>). <see cref="PreserveComposeNull" /> keeps a
///     Compose <c>null</c> (unset) value distinct from an empty string (upstream <c>preserve_compose_null</c>).
///     <see cref="SortOrder" /> is the page order; deployments write variables in that order.
/// </summary>
public sealed class EnvironmentVariable : Entity<EnvironmentVariableId>
{
    /// <summary>Longest plain value (characters).</summary>
    public const int MaxValueLength = 8000;

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
        int sortOrder,
        bool preserveComposeNull,
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
        SortOrder = ValidateSortOrder(sortOrder);
        PreserveComposeNull = preserveComposeNull && plainValue is null &&
                              valueKind == EnvironmentVariableValueKind.Plain;
        UsesLegacyEscaping = false;
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

    /// <summary>
    ///     True for variables saved before exact values: they keep the old env-file quoting so containers get the same
    ///     values after the upgrade. Cleared when the variable is saved again. Existing rows are migrated to true.
    /// </summary>
    public bool UsesLegacyEscaping { get; private set; }

    /// <summary>A plain variable without a value: Compose treats it as unset, not as an empty string.</summary>
    public bool PreserveComposeNull { get; private set; }

    /// <summary>Page order within the variable's scope; deployments write variables in this order.</summary>
    public int SortOrder { get; private set; }

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
        DateTimeOffset now,
        int sortOrder = 0,
        bool preserveComposeNull = false)
    {
        ValidateValue(valueKind, plainValue, secretReferenceId, preserveComposeNull);

        return new EnvironmentVariable(EnvironmentVariableId.New(), teamId, targetType, key, valueKind,
            NormalizePlainValue(plainValue), secretReferenceId, isBuildTime, isRuntime, isPreview, isLiteral,
            isMultiline, comment, sortOrder, preserveComposeNull, now);
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
        ValidateValue(valueKind, plainValue, secretReferenceId, PreserveComposeNull);
        ValueKind = valueKind;
        PlainValue = NormalizePlainValue(plainValue);
        SecretReferenceId = secretReferenceId;
        IsBuildTime = isBuildTime;
        IsRuntime = isRuntime;
        IsPreview = isPreview;
        IsLiteral = isLiteral;
        IsMultiline = isMultiline;
        Comment = DomainValidation.Optional(comment, nameof(Comment), 1000);
        // A saved variable uses exact values from now on, and a value replaces a preserved Compose null (upstream
        // clears preserve_compose_null when a value is set).
        UsesLegacyEscaping = false;
        if (PlainValue is not null || valueKind != EnvironmentVariableValueKind.Plain) PreserveComposeNull = false;
        Touch(now);
    }

    /// <summary>Moves the variable in the page order of its scope.</summary>
    public void Reorder(int sortOrder, DateTimeOffset now)
    {
        SortOrder = ValidateSortOrder(sortOrder);
        Touch(now);
    }

    private static int ValidateSortOrder(int sortOrder)
    {
        if (sortOrder is < 0 or > 1_000_000) throw new DomainException("Environment variable order is invalid.");
        return sortOrder;
    }

    private static void ValidateValue(
        EnvironmentVariableValueKind valueKind,
        string? plainValue,
        SecretReferenceId? secretReferenceId,
        bool preserveComposeNull)
    {
        if (valueKind == EnvironmentVariableValueKind.Secret && !secretReferenceId.HasValue)
            throw new DomainException("Secret environment variables require a secret reference.");

        if (valueKind != EnvironmentVariableValueKind.Secret && secretReferenceId.HasValue)
            throw new DomainException("Only secret environment variables can reference secret storage.");

        if (valueKind != EnvironmentVariableValueKind.Secret && plainValue is null &&
            !(preserveComposeNull && valueKind == EnvironmentVariableValueKind.Plain))
            throw new DomainException("Plain and shared environment variables require a value (it may be empty).");
    }

    /// <summary>
    ///     Plain values are kept exactly: empty values are allowed and nothing is trimmed (DEP-10). Only the length and
    ///     NUL characters (which no process environment can carry) are rejected.
    /// </summary>
    private static string? NormalizePlainValue(string? value)
    {
        if (value is null) return null;
        if (value.Length > MaxValueLength)
            throw new DomainException($"{nameof(PlainValue)} must be {MaxValueLength} characters or fewer.");
        if (value.Contains('\0', StringComparison.Ordinal))
            throw new DomainException("Environment variable values must not contain NUL characters.");
        return value;
    }
}
