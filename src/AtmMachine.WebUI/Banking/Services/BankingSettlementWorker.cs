using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using AtmMachine.WebUI.Observability;

namespace AtmMachine.WebUI.Banking.Services;

public sealed class BankingSettlementWorker : BackgroundService
{
    private readonly BankingService _bankingService;
    private readonly BankingTelemetry _telemetry;
    private readonly ILogger<BankingSettlementWorker> _logger;

    public BankingSettlementWorker(
        BankingService bankingService,
        BankingTelemetry telemetry,
        ILogger<BankingSettlementWorker> logger)
    {
        _bankingService = bankingService;
        _telemetry = telemetry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using Activity? activity = _telemetry.ActivitySource.StartActivity(
                "banking.settlement.run",
                ActivityKind.Internal);
            activity?.SetTag("worker.name", nameof(BankingSettlementWorker));

            try
            {
                int settled = await _bankingService.RunSettlementAsync(stoppingToken);
                _telemetry.RecordSettlementRun(settled, success: true);
                activity?.SetTag("settlement.processed_count", settled);
                if (settled > 0)
                {
                    _logger.LogInformation("Banking settlement worker processed {Count} transfer item(s).", settled);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                _telemetry.RecordSettlementRun(0, success: false);
                _telemetry.RecordBackgroundWorkerFailure();
                activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
                _logger.LogError(exception, "Settlement worker failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
    }
}
