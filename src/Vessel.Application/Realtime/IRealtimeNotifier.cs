namespace Vessel.Application.Realtime;

public interface IRealtimeNotifier
{
    Task PublishAsync(RealtimeGroup group, RealtimeMessage message, CancellationToken cancellationToken = default);
}
