using Microsoft.AspNetCore.SignalR;

namespace AtmMachine.WebUI.Realtime;

public sealed class BankingRealtimeDispatchWorker : BackgroundService
{
    private readonly BankingRealtimeQueue _queue;
    private readonly IHubContext<BankingHub> _hubContext;
    private readonly ILogger<BankingRealtimeDispatchWorker> _logger;

    public BankingRealtimeDispatchWorker(
        BankingRealtimeQueue queue,
        IHubContext<BankingHub> hubContext,
        ILogger<BankingRealtimeDispatchWorker> logger)
    {
        _queue = queue;
        _hubContext = hubContext;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            List<RealtimeEnvelope> batch = _queue.Drain(maxItems: 64);
            if (batch.Count == 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(350), stoppingToken);
                continue;
            }

            foreach (RealtimeEnvelope item in batch)
            {
                try
                {
                    await _hubContext.Clients.Group(item.GroupName)
                        .SendAsync(item.EventName, item.Payload, stoppingToken);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Failed to dispatch realtime event. GroupName={GroupName} EventName={EventName}",
                        item.GroupName,
                        item.EventName);
                }
            }
        }
    }
}
