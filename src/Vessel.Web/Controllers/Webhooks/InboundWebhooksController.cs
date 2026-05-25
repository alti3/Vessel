using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Vessel.Application.Webhooks;
using Vessel.Domain.Webhooks;

namespace Vessel.Web.Controllers.Webhooks;

[ApiController]
[EnableRateLimiting("webhooks")]
[Route("webhooks")]
public sealed class InboundWebhooksController(WebhookReceiptService receipts) : ControllerBase
{
    [HttpPost("github")]
    public Task<ActionResult<WebhookReceiptResult>> GitHub(CancellationToken cancellationToken)
    {
        return ReceiveAsync(WebhookProvider.GitHub, cancellationToken);
    }

    [HttpPost("gitlab")]
    public Task<ActionResult<WebhookReceiptResult>> GitLab(CancellationToken cancellationToken)
    {
        return ReceiveAsync(WebhookProvider.GitLab, cancellationToken);
    }

    [HttpPost("gitea")]
    public Task<ActionResult<WebhookReceiptResult>> Gitea(CancellationToken cancellationToken)
    {
        return ReceiveAsync(WebhookProvider.Gitea, cancellationToken);
    }

    [HttpPost("bitbucket")]
    public Task<ActionResult<WebhookReceiptResult>> Bitbucket(CancellationToken cancellationToken)
    {
        return ReceiveAsync(WebhookProvider.Bitbucket, cancellationToken);
    }

    [HttpPost("generic")]
    public Task<ActionResult<WebhookReceiptResult>> Generic(CancellationToken cancellationToken)
    {
        return ReceiveAsync(WebhookProvider.Generic, cancellationToken);
    }

    private async Task<ActionResult<WebhookReceiptResult>> ReceiveAsync(
        WebhookProvider provider,
        CancellationToken cancellationToken)
    {
        string rawBody;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8))
        {
            rawBody = await reader.ReadToEndAsync(cancellationToken);
        }

        var headers = Request.Headers
            .ToDictionary(header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase);

        WebhookReceiptResult result = await receipts.ReceiveAsync(
            new WebhookReceiptRequest(provider, headers, string.IsNullOrWhiteSpace(rawBody) ? "{}" : rawBody),
            cancellationToken);

        return result.Status switch
        {
            WebhookEventStatus.Duplicate => Ok(result),
            _ => Accepted(result)
        };
    }
}
