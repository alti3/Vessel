using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vessel.Application.Terminals;

namespace Vessel.Infrastructure.Processes;

/// <summary>
///     Periodically re-authorizes live terminal sessions (<see cref="TerminalSessionManager.RevalidateAsync" />), so a
///     session ends shortly after its user loses access, the server disables terminals, or the session goes idle.
/// </summary>
public sealed partial class TerminalSessionRevalidationService(
    IServiceScopeFactory scopes,
    TimeProvider timeProvider,
    ILogger<TerminalSessionRevalidationService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            try
            {
                await using AsyncServiceScope scope = scopes.CreateAsyncScope();
                var manager = scope.ServiceProvider.GetRequiredService<TerminalSessionManager>();
                await manager.RevalidateAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogRevalidationFailed(logger, ex);
            }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Terminal session re-authorization failed.")]
    private static partial void LogRevalidationFailed(ILogger logger, Exception exception);
}
