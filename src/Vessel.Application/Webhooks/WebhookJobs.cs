namespace Vessel.Application.Webhooks;

public sealed class ProcessWebhookEventJob(WebhookProcessingService processor)
{
    public Task ProcessAsync(Guid webhookEventId, CancellationToken cancellationToken = default)
    {
        return processor.ProcessAsync(new Domain.WebhookEventId(webhookEventId), cancellationToken);
    }
}
