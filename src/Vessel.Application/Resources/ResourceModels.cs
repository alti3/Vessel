using Vessel.Domain.Applications;
using Vessel.Domain.Databases;
using Vessel.Domain.EnvironmentVariables;
using Vessel.Domain.Projects;
using Vessel.Domain.Servers;

namespace Vessel.Application.Resources;

public sealed record ProjectSummary(Guid Id, string Name, string? Description, bool IsArchived, int EnvironmentCount);

public sealed record ProjectDetails(
    Guid Id,
    string Name,
    string? Description,
    bool IsArchived,
    IReadOnlyList<EnvironmentSummary> Environments);

public sealed record EnvironmentSummary(
    Guid Id,
    Guid ProjectId,
    string Name,
    EnvironmentKind Kind,
    string? Description);

public sealed record ServerSummary(
    Guid Id,
    string Name,
    string? Description,
    string Address,
    ServerConnectionType ConnectionType,
    ContainerRuntimeKind Runtime,
    ServerStatus Status,
    string Labels);

public sealed record ServerConnectivityResult(
    Guid ServerId,
    bool Reachable,
    ContainerRuntimeKind Runtime,
    string Message,
    DateTimeOffset CheckedAt);

public sealed record ServerStatusSnapshotSummary(
    Guid Id,
    Guid ServerId,
    ServerStatus Status,
    decimal? CpuLoadPercent,
    long? MemoryUsedBytes,
    long? DiskUsedBytes,
    int RunningContainers,
    bool ProxyHealthy,
    bool CertificatesHealthy,
    DateTimeOffset CreatedAt);

public sealed record ApplicationSummary(
    Guid Id,
    string Name,
    string? Description,
    Guid ProjectId,
    Guid EnvironmentId,
    Guid ServerId,
    string RepositoryUrl,
    string Branch,
    ApplicationBuildPack BuildPack,
    IReadOnlyList<string> Domains);

public sealed record DatabaseSummary(
    Guid Id,
    string Name,
    string? Description,
    Guid ProjectId,
    Guid EnvironmentId,
    Guid ServerId,
    DatabaseEngine Engine,
    string Version,
    DatabaseHealthState HealthState,
    Guid CredentialsReferenceId);

public sealed record EnvironmentVariableSummary(
    Guid Id,
    EnvironmentVariableTargetType TargetType,
    string Key,
    EnvironmentVariableValueKind ValueKind,
    string DisplayValue,
    bool CanReveal,
    bool IsBuildTime,
    bool IsRuntime,
    bool IsPreview,
    bool IsLiteral,
    bool IsMultiline,
    string? Comment);

public sealed record RegistryCredentialSummary(
    Guid Id,
    string Name,
    string Registry,
    string Username,
    Guid PasswordReferenceId);

public sealed record CreateProjectRequest(string Name, string? Description);

public sealed record UpdateProjectRequest(string Name, string? Description);

public sealed record CreateEnvironmentRequest(Guid ProjectId, string Name, EnvironmentKind Kind, string? Description);

public sealed record UpdateEnvironmentRequest(string Name, EnvironmentKind Kind, string? Description);

public sealed record CreateServerRequest(
    string Name,
    string? Description,
    string Host,
    int Port,
    string? User,
    ServerConnectionType ConnectionType,
    ContainerRuntimeKind Runtime,
    string? Labels);

public sealed record UpdateServerRequest(
    string Name,
    string? Description,
    string Host,
    int Port,
    string? User,
    ServerConnectionType ConnectionType,
    ContainerRuntimeKind Runtime,
    string? Labels);

public sealed record CreateApplicationRequest(
    Guid ProjectId,
    Guid EnvironmentId,
    Guid ServerId,
    string Name,
    string? Description,
    string RepositoryUrl,
    string Branch,
    ApplicationBuildPack BuildPack,
    string BaseDirectory,
    string? DockerfilePath,
    int? ExposedPort,
    string[] Domains);

public sealed record UpdateApplicationRequest(
    string Name,
    string? Description,
    string RepositoryUrl,
    string Branch,
    ApplicationBuildPack BuildPack,
    string BaseDirectory,
    string? DockerfilePath,
    int? ExposedPort,
    string[] Domains);

public sealed record CreateDatabaseRequest(
    Guid ProjectId,
    Guid EnvironmentId,
    Guid ServerId,
    string Name,
    string? Description,
    DatabaseEngine Engine,
    string Version,
    string VolumeName,
    string MountPath,
    string Credentials);

public sealed record CreateEnvironmentVariableRequest(
    EnvironmentVariableTargetType TargetType,
    Guid? ProjectId,
    Guid? EnvironmentId,
    Guid? ServerId,
    Guid? ApplicationId,
    Guid? DatabaseResourceId,
    string Key,
    string Value,
    EnvironmentVariableValueKind ValueKind,
    bool IsBuildTime,
    bool IsRuntime,
    bool IsPreview,
    bool IsLiteral,
    bool IsMultiline,
    string? Comment);

public sealed record UpdateEnvironmentVariableRequest(
    string Value,
    EnvironmentVariableValueKind ValueKind,
    bool IsBuildTime,
    bool IsRuntime,
    bool IsPreview,
    bool IsLiteral,
    bool IsMultiline,
    string? Comment);

public sealed record CreateRegistryCredentialRequest(
    string Name,
    string Registry,
    string Username,
    string Password);
