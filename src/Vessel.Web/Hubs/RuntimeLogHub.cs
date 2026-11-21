using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Vessel.Application.Authorization;
using Vessel.Application.Resources.RuntimeLogs;
using Vessel.Domain.Monitoring;
using Vessel.Web.Security;

namespace Vessel.Web.Hubs;

/// <summary>
///     Live follow of runtime logs (SRV-05). The browser sends only the resource kind and id; the Application service
///     authorizes (read-sensitive <see cref="VesselPermissions.ResourcesReadLogs" />, owning team, host-local admin),
///     resolves the containers server-side, redacts every line, and re-checks access while the stream runs. The stream
///     ends when the client cancels or disconnects.
/// </summary>
[Authorize(Policy = VesselPermissions.ResourcesReadLogs)]
public sealed class RuntimeLogHub(RuntimeLogQueryService logs) : Hub
{
    public async IAsyncEnumerable<RuntimeLogLineDto> Follow(string kind, Guid resourceId, int? tail = null,
        bool timestamps = true, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ResourceRuntimeKind resourceKind = kind switch
        {
            "application" or "applications" => ResourceRuntimeKind.Application,
            "database" or "databases" => ResourceRuntimeKind.Database,
            "service" or "services" => ResourceRuntimeKind.Service,
            _ => throw new HubException("Unknown resource kind.")
        };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,
            Context.ConnectionAborted);
        IAsyncEnumerable<RuntimeLogLineDto> stream;
        try
        {
            stream = logs.FollowAsync(Context.User!.GetUserId(), Context.User!.GetTeamId(), resourceKind,
                resourceId, tail, timestamps, linked.Token);
        }
        catch (UnauthorizedAccessException)
        {
            throw new HubException("Not authorized to read this resource's logs.");
        }

        await using IAsyncEnumerator<RuntimeLogLineDto> enumerator = stream.GetAsyncEnumerator(linked.Token);
        while (true)
        {
            bool moved;
            try
            {
                moved = await enumerator.MoveNextAsync();
            }
            catch (UnauthorizedAccessException)
            {
                throw new HubException("Not authorized to read this resource's logs.");
            }
            catch (KeyNotFoundException exception)
            {
                throw new HubException(exception.Message);
            }

            if (!moved) yield break;
            yield return enumerator.Current;
        }
    }
}
