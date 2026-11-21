using Vessel.Application.Auditing;
using Vessel.Application.Persistence;
using Vessel.Application.Redis;
using Vessel.Application.Servers;
using Vessel.Domain;
using Vessel.Domain.Auditing;
using Vessel.Domain.Servers;
using Vessel.Domain.Ssh;

namespace Vessel.Application.Ssh.Keys;

/// <summary>
///     Moves every SSH server of a team from one key to another (upstream Coolify v4.7 "Rotate key"): add the new public
///     key to <c>~/.ssh/authorized_keys</c> over the old key, log in with the new key, switch the server, then remove the
///     old key line over the new key. A server whose new key cannot log in keeps the old key (the added line is undone),
///     so the rotation can run again.
/// </summary>
public sealed class SshKeyRotationService(
    IVesselDbContext dbContext,
    IServerCommandRunner commands,
    IDistributedLockManager locks,
    IAuditWriter auditWriter,
    TimeProvider timeProvider)
{
    /// <summary>Appends <c>$1</c> to authorized_keys unless the exact line is already present.</summary>
    public const string AddAuthorizedKeyScript =
        "set -eu; umask 077; key=$1; dir=\"$HOME/.ssh\"; file=\"$dir/authorized_keys\"; mkdir -p \"$dir\"; touch \"$file\"; " +
        "if grep -qxF -- \"$key\" \"$file\"; then exit 0; fi; " +
        "if [ -s \"$file\" ] && [ -n \"$(tail -c 1 \"$file\")\" ]; then printf '\\n' >> \"$file\"; fi; " +
        "printf '%s\\n' \"$key\" >> \"$file\"";

    /// <summary>Removes every authorized_keys line that contains the key data <c>$1</c> (in place, keeping the file).</summary>
    public const string RemoveAuthorizedKeyScript =
        "set -eu; blob=$1; file=\"$HOME/.ssh/authorized_keys\"; [ -f \"$file\" ] || exit 0; " +
        "tmp=\"$file.vessel.$$\"; " +
        "awk -v blob=\"$blob\" '{ keep = 1; for (i = 1; i <= NF; i++) if ($i == blob) keep = 0; if (keep) print }' \"$file\" > \"$tmp\"; " +
        "cat \"$tmp\" > \"$file\"; rm -f \"$tmp\"";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(1);

    public async Task<IReadOnlyList<SshKeyRotationServerResult>> RotateAsync(
        TeamId teamId,
        SshKeyId oldKeyId,
        SshKeyId newKeyId,
        UserId? actorUserId,
        CancellationToken cancellationToken = default)
    {
        SshKey oldKey = dbContext.SshKeys.SingleOrDefault(key => key.Id == oldKeyId && key.TeamId == teamId)
                        ?? throw new KeyNotFoundException("The SSH key to rotate was not found.");
        SshKey newKey = dbContext.SshKeys.SingleOrDefault(key => key.Id == newKeyId && key.TeamId == teamId)
                        ?? throw new KeyNotFoundException("The new SSH key was not found.");

        await using DistributedLockHandle? handle = await locks.TryAcquireAsync(
            $"ssh-key-rotation:{oldKeyId.Value:D}", TimeSpan.FromMinutes(30), TimeSpan.Zero, cancellationToken);
        if (handle is null)
            throw new InvalidOperationException("A rotation of this SSH key is already running.");

        Server[] servers = dbContext.Servers
            .Where(server => server.TeamId == teamId && server.SshKeyId == oldKeyId &&
                             server.ConnectionType == ServerConnectionType.Ssh)
            .ToArray();
        SshPublicKeyInfo oldPublic = SshKeyFormats.ParsePublicKey(oldKey.PublicKey);
        SshPublicKeyInfo newPublic = SshKeyFormats.ParsePublicKey(newKey.PublicKey);

        List<SshKeyRotationServerResult> results = [];
        foreach (Server server in servers)
        {
            SshKeyRotationServerResult result =
                await RotateServerAsync(server, newKey, oldPublic, newPublic, cancellationToken);
            results.Add(result);
            await auditWriter.RecordAsync(teamId, actorUserId, AuditActions.SshKeyRotated,
                new AuditTarget("server", server.Id.Value.ToString("D")), null,
                new Dictionary<string, object?>
                {
                    ["oldKeyId"] = oldKeyId.Value,
                    ["newKeyId"] = newKeyId.Value,
                    ["status"] = result.Status.ToString()
                }, cancellationToken);
        }

        return results;
    }

    private async Task<SshKeyRotationServerResult> RotateServerAsync(
        Server server,
        SshKey newKey,
        SshPublicKeyInfo oldPublic,
        SshPublicKeyInfo newPublic,
        CancellationToken cancellationToken)
    {
        ServerExecutionTarget target = ServerExecutionTarget.From(server);
        ServerCommandResult add;
        try
        {
            add = await commands.RunAsync(target, ["sh", "-c", AddAuthorizedKeyScript, "vessel", newPublic.KeyLine],
                CommandTimeout, null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result(server, SshKeyRotationStatus.Unreachable, "Could not connect with the current key.");
        }

        if (!add.Succeeded)
            return Result(server, add.ExitCode == 255 ? SshKeyRotationStatus.Unreachable : SshKeyRotationStatus.Failed,
                "Could not add the new key to authorized_keys.");

        ServerCommandResult probe;
        try
        {
            probe = await commands.RunWithKeyAsync(target, newKey.Id, ["true"], CommandTimeout, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            probe = new ServerCommandResult(255, string.Empty, string.Empty);
        }

        if (!probe.Succeeded)
        {
            try
            {
                await commands.RunAsync(target,
                    ["sh", "-c", RemoveAuthorizedKeyScript, "vessel", newPublic.Base64Blob], CommandTimeout, null,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The server stays on the old key; a later rotation adds the same new key again.
            }

            return Result(server, SshKeyRotationStatus.NewKeyRejected,
                "The new key could not log in. Check the AuthorizedKeysFile setting of sshd.");
        }

        server.AssignSshKey(newKey.Id, timeProvider.GetUtcNow());
        await dbContext.SaveChangesAsync(cancellationToken);

        if (string.Equals(oldPublic.Base64Blob, newPublic.Base64Blob, StringComparison.Ordinal))
            return Result(server, SshKeyRotationStatus.Moved, "The server uses the new key.");
        try
        {
            ServerCommandResult remove = await commands.RunAsync(ServerExecutionTarget.From(server),
                ["sh", "-c", RemoveAuthorizedKeyScript, "vessel", oldPublic.Base64Blob], CommandTimeout, null,
                cancellationToken);
            if (remove.Succeeded)
                return Result(server, SshKeyRotationStatus.Moved,
                    "The server uses the new key and the old key was removed from authorized_keys.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reported below.
        }

        return Result(server, SshKeyRotationStatus.MovedOldKeyNotRemoved,
            "The server uses the new key, but the old key could not be removed from authorized_keys.");
    }

    private static SshKeyRotationServerResult Result(Server server, SshKeyRotationStatus status, string message)
    {
        return new SshKeyRotationServerResult(server.Id.Value, server.Name.Value, status, message);
    }
}

/// <summary>Hangfire entry point for <see cref="SshKeyRotationService" />.</summary>
public sealed class RotateSshKeyJob(SshKeyRotationService rotation)
{
    public Task RunAsync(Guid teamId, Guid oldKeyId, Guid newKeyId, Guid? actorUserId,
        CancellationToken cancellationToken = default)
    {
        return rotation.RotateAsync(new TeamId(teamId), new SshKeyId(oldKeyId), new SshKeyId(newKeyId),
            actorUserId.HasValue ? new UserId(actorUserId.Value) : null, cancellationToken);
    }
}
