using Vessel.Domain.Databases;

namespace Vessel.Application.ManagedServices;

public enum DatabaseLifecycleAction
{
    Start,
    Stop,
    Restart,
    Delete,
    Inspect
}

public sealed record DatabaseLifecycleResult(
    Guid DatabaseId,
    DatabaseLifecycleState State,
    DatabaseHealthState HealthState,
    string Message);

public sealed record ServiceTemplateSummary(
    string Key,
    string Name,
    string Description,
    string Version,
    IReadOnlyList<ServiceTemplateInput> Inputs);

public sealed record ServiceTemplateInput(
    string Key,
    string Label,
    bool Required,
    bool Secret,
    string? DefaultValue);

public sealed record CreateServiceFromTemplateRequest(
    Guid ProjectId,
    Guid EnvironmentId,
    Guid ServerId,
    string Name,
    string TemplateKey,
    IReadOnlyDictionary<string, string> Inputs);

public sealed record ServiceResourceSummary(
    Guid Id,
    string Name,
    Guid EnvironmentId,
    Guid ServerId,
    string TemplateKey,
    string TemplateVersion,
    DatabaseLifecycleState State);

public sealed record RestoreValidationResult(
    Guid BackupExecutionId,
    Guid TargetDatabaseId,
    bool DryRun,
    string ImpactSummary);

public sealed record DatabaseProvisioningPlan(
    string ProjectName,
    string ServiceName,
    string ContainerName,
    string VolumeName,
    string ComposeYaml,
    IReadOnlyDictionary<string, string> SecretValues);

public sealed record ServiceProvisioningPlan(
    string ProjectName,
    string ServiceName,
    string ComposeYaml,
    IReadOnlyDictionary<string, string> SecretValues);
