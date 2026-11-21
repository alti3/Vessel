using Microsoft.Extensions.Options;
using Vessel.Application.Auditing;
using Vessel.Application.Authorization;
using Vessel.Application.Jobs;
using Vessel.Application.Persistence;
using Vessel.Application.Servers.Validation;
using Vessel.Application.Ssh;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Common;
using Vessel.Domain.Servers;
using Vessel.Domain.Ssh;
using Vessel.Domain.ValueObjects;

namespace Vessel.Application.Servers;

/// <summary>
///     Server lifecycle (SRV-02/SEC-12): details, partial update, delete with in-use checks, validation requests, SSH key
///     assignment (same-team keys only), host key checking mode and host key trust/reset, and the per-server terminal
///     switch.
/// </summary>
public sealed class ServerManagementService(
    IVesselDbContext dbContext,
    VesselAuthorizationService authorization,
    IBackgroundJobDispatcher jobs,
    SshHostKeyScanThrottle scanThrottle,
    IAuditWriter auditWriter,
    IOptions<SshOptions> sshOptions,
    TimeProvider timeProvider)
{
    public ServerDetails Get(UserId actorUserId, TeamId teamId, ServerId serverId)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Server(serverId), VesselPermissions.ServersRead);
        return ToDetails(Load(serverId));
    }

    public async Task<ServerDetails> UpdateAsync(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId,
        PatchServerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        authorization.Demand(actorUserId, teamId, ResourceRef.Server(serverId), VesselPermissions.ServersWrite);
        Server server = Load(serverId);
        HostLocalExecutionGuard.RequireInstanceAdmin(authorization, actorUserId, server);
        DateTimeOffset now = timeProvider.GetUtcNow();
        Dictionary<string, object?> changes = [];

        var host = request.Host ?? server.Address.Host;
        var port = request.Port ?? server.Address.Port.Value;
        var user = request.User is null ? server.Address.User : string.IsNullOrWhiteSpace(request.User) ? null : request.User;
        var addressChanged = !string.Equals(host, server.Address.Host, StringComparison.Ordinal) ||
                             port != server.Address.Port.Value;
        if (server.ConnectionType == ServerConnectionType.Ssh &&
            (!SshTargetValidator.IsSafeHost(host) || (user is not null && !SshTargetValidator.IsSafeUser(user))))
            throw new DomainException("Server address or user contains characters that are not allowed for SSH.");

        server.UpdateSettings(
            request.Name is null ? server.Name : new ResourceName(request.Name),
            request.Description is null ? server.Description :
            string.IsNullOrWhiteSpace(request.Description) ? null : new Description(request.Description),
            new ServerAddress(host, new PortNumber(port), user),
            server.ConnectionType,
            server.Runtime,
            request.Labels ?? server.Labels,
            now);
        if (addressChanged)
        {
            // The pinned host key belongs to the old address: the next connection pins the new server's key.
            server.ResetHostKey(now);
            scanThrottle.Reset(server.Id);
            changes["hostKeyReset"] = true;
        }

        if (request.ClearSshKey)
        {
            server.AssignSshKey(null, now);
            changes["sshKeyId"] = null;
        }
        else if (request.SshKeyId.HasValue)
        {
            var keyId = new SshKeyId(request.SshKeyId.Value);
            // Only keys of the server's own team may be selected; another team's key looks like a missing key.
            if (!dbContext.SshKeys.Any(key => key.Id == keyId && key.TeamId == server.TeamId))
                throw new KeyNotFoundException("SSH key was not found.");
            server.AssignSshKey(keyId, now);
            changes["sshKeyId"] = keyId.Value;
        }

        if (request.HostKeyChecking.HasValue)
        {
            SshHostKeyCheckingMode instanceMode = sshOptions.Value.ResolveInstanceMode();
            // Teams may make a server stricter than the instance default, never looser (instance admins excepted).
            if (request.HostKeyChecking.Value < instanceMode && request.HostKeyChecking.Value < server.HostKeyChecking &&
                !authorization.IsInstanceAdmin(actorUserId))
                throw new UnauthorizedAccessException(
                    "Only an instance administrator can make SSH host key checking looser than the instance default.");
            server.SetHostKeyChecking(request.HostKeyChecking.Value, now);
            changes["hostKeyChecking"] = request.HostKeyChecking.Value.ToString();
        }

        if (request.TerminalAccessEnabled.HasValue)
        {
            server.SetTerminalAccess(request.TerminalAccessEnabled.Value, now);
            changes["terminalAccessEnabled"] = request.TerminalAccessEnabled.Value;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await AuditAsync(server, actorUserId, AuditActions.ServerUpdated, changes, cancellationToken);
        return ToDetails(server);
    }

    public async Task DeleteAsync(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId,
        CancellationToken cancellationToken = default)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Server(serverId), VesselPermissions.ServersWrite);
        Server server = Load(serverId);
        HostLocalExecutionGuard.RequireInstanceAdmin(authorization, actorUserId, server);

        var applications = dbContext.Applications.Count(application => application.ServerId == serverId);
        var databases = dbContext.DatabaseResources.Count(database => database.ServerId == serverId);
        var services = dbContext.ServiceResources.Count(service => service.ServerId == serverId);
        var deployments = dbContext.Deployments.Count(deployment => deployment.ServerId == serverId);
        var variables = dbContext.EnvironmentVariables.Count(variable => variable.ServerId == serverId);
        if (applications + databases + services + deployments + variables > 0)
            throw new ServerInUseException(
                $"The server is still in use ({applications} application(s), {databases} database(s), {services} service(s), " +
                $"{deployments} deployment(s), {variables} server variable(s)). Move or delete them first.");

        dbContext.ServerRepository.Remove(server);
        await dbContext.SaveChangesAsync(cancellationToken);
        scanThrottle.Reset(server.Id);
        await AuditAsync(server, actorUserId, AuditActions.ServerDeleted, new Dictionary<string, object?>(),
            cancellationToken);
    }

    /// <summary>Queues a validation job (POST only). Docker is installed only with <paramref name="request" />.InstallDocker.</summary>
    public async Task<ServerValidationAccepted> RequestValidationAsync(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId,
        ValidateServerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        authorization.Demand(actorUserId, teamId, ResourceRef.Server(serverId), VesselPermissions.ServersWrite);
        Server server = Load(serverId);
        HostLocalExecutionGuard.RequireInstanceAdmin(authorization, actorUserId, server);
        var jobId = jobs.Enqueue<ValidateServerJob>(job =>
            job.RunAsync(serverId.Value, request.InstallDocker, actorUserId.Value, CancellationToken.None));
        await AuditAsync(server, actorUserId, AuditActions.ServerValidationRequested,
            new Dictionary<string, object?> { ["installDocker"] = request.InstallDocker }, cancellationToken);
        return new ServerValidationAccepted(server.Id.Value, jobId, request.InstallDocker);
    }

    /// <summary>Trusts the pending (changed) host key after the operator verified it out of band.</summary>
    public async Task<ServerDetails> TrustPendingHostKeyAsync(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId,
        CancellationToken cancellationToken = default)
    {
        Server server = RequireHostKeyAdmin(actorUserId, teamId, serverId);
        IReadOnlyList<string> fingerprints = SshKeyFormats.HostKeyFingerprints(server.PendingHostKey);
        server.TrustPendingHostKey(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        scanThrottle.Reset(server.Id);
        await AuditAsync(server, actorUserId, AuditActions.ServerHostKeyTrusted,
            new Dictionary<string, object?> { ["fingerprints"] = fingerprints }, cancellationToken);
        return ToDetails(server);
    }

    /// <summary>Forgets the pinned host key; the next connection pins the key again (trust on first use).</summary>
    public async Task<ServerDetails> ResetHostKeyAsync(
        UserId actorUserId,
        TeamId teamId,
        ServerId serverId,
        CancellationToken cancellationToken = default)
    {
        Server server = RequireHostKeyAdmin(actorUserId, teamId, serverId);
        server.ResetHostKey(timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);
        scanThrottle.Reset(server.Id);
        await AuditAsync(server, actorUserId, AuditActions.ServerHostKeyReset, new Dictionary<string, object?>(),
            cancellationToken);
        return ToDetails(server);
    }

    private Server RequireHostKeyAdmin(UserId actorUserId, TeamId teamId, ServerId serverId)
    {
        authorization.Demand(actorUserId, teamId, ResourceRef.Server(serverId), VesselPermissions.ServersWrite);
        Server server = Load(serverId);
        if (server.ConnectionType != ServerConnectionType.Ssh)
            throw new DomainException("Only SSH servers have host keys.");
        return server;
    }

    private Server Load(ServerId serverId)
    {
        return dbContext.Servers.SingleOrDefault(server => server.Id == serverId)
               ?? throw new KeyNotFoundException("Server was not found.");
    }

    private ServerDetails ToDetails(Server server)
    {
        ServerValidation? validation = server.Validation;
        return new ServerDetails(
            server.Id.Value,
            server.Name.Value,
            server.Description?.Value,
            server.Address.Host,
            server.Address.Port.Value,
            server.Address.User,
            server.ConnectionType,
            server.Runtime,
            server.Status,
            server.Labels,
            server.SshKeyId?.Value,
            server.HostKeyChecking,
            SshOptions.Effective(sshOptions.Value.ResolveInstanceMode(), server.HostKeyChecking),
            SafeFingerprints(server.PinnedHostKey),
            SafeFingerprints(server.PendingHostKey),
            server.HostKeyTrustedAt,
            server.HostKeyChangedAt,
            server.TerminalAccessEnabled,
            validation is null
                ? null
                : new ServerValidationDetails(validation.Succeeded, validation.OsId, validation.OsIdLike,
                    validation.OsVersion, validation.PackageManager, validation.RuntimeVersion,
                    validation.ComposeVersion, validation.IsRootUser, validation.HasPasswordlessSudo,
                    validation.Message, validation.ValidatedAt),
            server.CreatedAt,
            server.UpdatedAt);
    }

    private static IReadOnlyList<string> SafeFingerprints(string? hostKeys)
    {
        try
        {
            return SshKeyFormats.HostKeyFingerprints(hostKeys);
        }
        catch (FormatException)
        {
            return [];
        }
    }

    private Task AuditAsync(Server server, UserId actorUserId, string action, Dictionary<string, object?> metadata,
        CancellationToken cancellationToken)
    {
        return auditWriter.RecordAsync(server.TeamId, actorUserId, action,
            new AuditTarget("server", server.Id.Value.ToString("D")), null, metadata, cancellationToken);
    }
}
