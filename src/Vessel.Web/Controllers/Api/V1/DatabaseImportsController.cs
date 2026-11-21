using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Vessel.Application.Authorization;
using Vessel.Application.ManagedServices.DatabaseImports;
using Vessel.Domain;
using Vessel.Web.Security;

namespace Vessel.Web.Controllers.Api.V1;

/// <summary>
///     Database imports (DATA-10). Uploads are chunked: start an upload, <c>PUT</c> each chunk as the raw request body
///     with its <c>offset</c> (sequential, at most <see cref="DatabaseImportService.MaxChunkBytes" />), complete it,
///     then start the import. Imports from a server path require an instance administrator. Progress and logs are on
///     <c>GET …/imports/{importId}</c> (and the <c>database.restore</c> realtime event).
/// </summary>
[ApiController]
[Authorize(Policy = VesselPermissions.ProjectsRead)]
[Route("api/v1/databases/{databaseId:guid}/imports")]
public sealed class DatabaseImportsController(DatabaseImportService imports) : ControllerBase
{
    [HttpPost("uploads")]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public Task<ActionResult> StartUpload(Guid databaseId, StartDatabaseImportUploadRequest request,
        CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Ok(await imports.StartUploadAsync(User.GetUserId(), User.GetTeamId(),
            new DatabaseResourceId(databaseId), request, cancellationToken)));
    }

    [HttpGet("uploads/{uploadId:guid}")]
    public Task<ActionResult> GetUpload(Guid databaseId, Guid uploadId, CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Ok(await imports.GetUploadSummaryAsync(User.GetUserId(), User.GetTeamId(),
            new DatabaseResourceId(databaseId), new DatabaseImportUploadId(uploadId), cancellationToken)));
    }

    /// <summary>Appends one chunk (raw body) at <paramref name="offset" />; the body is streamed to staging.</summary>
    [HttpPut("uploads/{uploadId:guid}/chunks")]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    [RequestSizeLimit(DatabaseImportService.MaxChunkBytes + 1024)]
    public Task<ActionResult> AppendChunk(Guid databaseId, Guid uploadId, [FromQuery] long offset,
        CancellationToken cancellationToken)
    {
        if (Request.ContentLength is > DatabaseImportService.MaxChunkBytes)
            return Task.FromResult<ActionResult>(StatusCode(StatusCodes.Status413PayloadTooLarge));
        return GuardAsync(async () => Ok(await imports.AppendChunkAsync(User.GetUserId(), User.GetTeamId(),
            new DatabaseResourceId(databaseId), new DatabaseImportUploadId(uploadId), offset, Request.Body,
            cancellationToken)));
    }

    [HttpPost("uploads/{uploadId:guid}/complete")]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public Task<ActionResult> CompleteUpload(Guid databaseId, Guid uploadId, CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Ok(await imports.CompleteUploadAsync(User.GetUserId(), User.GetTeamId(),
            new DatabaseResourceId(databaseId), new DatabaseImportUploadId(uploadId), cancellationToken)));
    }

    [HttpDelete("uploads/{uploadId:guid}")]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public Task<ActionResult> CancelUpload(Guid databaseId, Guid uploadId, CancellationToken cancellationToken)
    {
        return GuardAsync(async () =>
        {
            await imports.CancelUploadAsync(User.GetUserId(), User.GetTeamId(), new DatabaseResourceId(databaseId),
                new DatabaseImportUploadId(uploadId), cancellationToken);
            return NoContent();
        });
    }

    /// <summary>Queues an import (202) from an upload, a server path or a backup destination object.</summary>
    [HttpPost]
    [Authorize(Policy = VesselPermissions.ProjectsWrite)]
    public Task<ActionResult> Start(Guid databaseId, QueueDatabaseImportRequest request,
        CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Accepted(await imports.QueueImportAsync(User.GetUserId(), User.GetTeamId(),
            new DatabaseResourceId(databaseId), request, cancellationToken)));
    }

    [HttpGet("{importId:guid}")]
    public Task<ActionResult> Get(Guid databaseId, Guid importId, CancellationToken cancellationToken)
    {
        return GuardAsync(async () => Ok(await imports.GetImportAsync(User.GetUserId(), User.GetTeamId(),
            new DatabaseResourceId(databaseId), new DatabaseRestoreId(importId), cancellationToken)));
    }

    private async Task<ActionResult> GuardAsync(Func<Task<ActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception exception) when (StorageApiErrors.Map(this, exception) is { } result)
        {
            return result;
        }
    }
}
