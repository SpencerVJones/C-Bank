using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;

namespace AtmMachine.WebUI.Observability;

public sealed class BankingTelemetry : IDisposable
{
    private const string MeterName = "ConsoleATM.Banking";
    private const string ActivitySourceName = "ConsoleATM.Banking";

    private readonly Counter<long> _httpRequests;
    private readonly Counter<long> _httpErrors;
    private readonly Histogram<double> _httpDurationMs;
    private readonly Counter<long> _transferRequests;
    private readonly Counter<long> _transferSuccesses;
    private readonly Counter<long> _transferFailures;
    private readonly Counter<long> _fundMutations;
    private readonly Counter<long> _fundMutationFailures;
    private readonly Counter<long> _disputesCreated;
    private readonly Counter<long> _disputeFailures;
    private readonly Counter<long> _settlementRuns;
    private readonly Counter<long> _settlementSuccesses;
    private readonly Counter<long> _settlementFailures;
    private readonly Counter<long> _settlementTransfersProcessed;
    private readonly Counter<long> _backgroundWorkerFailures;

    private long _httpRequestCount;
    private long _httpErrorCount;
    private long _httpDurationSamples;
    private long _httpDurationMsTotal;
    private long _transferRequestCount;
    private long _transferSuccessCount;
    private long _transferFailureCount;
    private long _fundMutationCount;
    private long _fundMutationFailureCount;
    private long _disputesCreatedCount;
    private long _disputeFailureCount;
    private long _settlementRunCount;
    private long _settlementSuccessCount;
    private long _settlementFailureCount;
    private long _settlementTransfersProcessedCount;
    private long _backgroundWorkerFailureCount;

    public BankingTelemetry()
    {
        ActivitySource = new ActivitySource(ActivitySourceName);
        Meter = new Meter(MeterName);

        _httpRequests = Meter.CreateCounter<long>("consoleatm.http.requests");
        _httpErrors = Meter.CreateCounter<long>("consoleatm.http.errors");
        _httpDurationMs = Meter.CreateHistogram<double>("consoleatm.http.duration.ms");
        _transferRequests = Meter.CreateCounter<long>("consoleatm.transfer.requests");
        _transferSuccesses = Meter.CreateCounter<long>("consoleatm.transfer.success");
        _transferFailures = Meter.CreateCounter<long>("consoleatm.transfer.failures");
        _fundMutations = Meter.CreateCounter<long>("consoleatm.funds.mutations");
        _fundMutationFailures = Meter.CreateCounter<long>("consoleatm.funds.mutation.failures");
        _disputesCreated = Meter.CreateCounter<long>("consoleatm.disputes.created");
        _disputeFailures = Meter.CreateCounter<long>("consoleatm.disputes.failures");
        _settlementRuns = Meter.CreateCounter<long>("consoleatm.settlement.runs");
        _settlementSuccesses = Meter.CreateCounter<long>("consoleatm.settlement.success");
        _settlementFailures = Meter.CreateCounter<long>("consoleatm.settlement.failures");
        _settlementTransfersProcessed = Meter.CreateCounter<long>("consoleatm.settlement.transfers.processed");
        _backgroundWorkerFailures = Meter.CreateCounter<long>("consoleatm.background.failures");
    }

    public ActivitySource ActivitySource { get; }

    public Meter Meter { get; }

    public void RecordHttpRequest(
        string method,
        string path,
        int statusCode,
        double elapsedMilliseconds,
        string correlationId)
    {
        TagList tags = new()
        {
            { "http.method", method },
            { "http.route", path },
            { "http.status_code", statusCode },
            { "correlation.id", correlationId }
        };

        _httpRequests.Add(1, tags);
        _httpDurationMs.Record(elapsedMilliseconds, tags);

        Interlocked.Increment(ref _httpRequestCount);
        Interlocked.Increment(ref _httpDurationSamples);
        Interlocked.Add(ref _httpDurationMsTotal, (long)Math.Round(elapsedMilliseconds, MidpointRounding.AwayFromZero));

        if (statusCode >= 500)
        {
            _httpErrors.Add(1, tags);
            Interlocked.Increment(ref _httpErrorCount);
        }
    }

    public void RecordTransfer(string kind, bool success)
    {
        TagList tags = new()
        {
            { "transfer.kind", kind },
            { "success", success }
        };

        _transferRequests.Add(1, tags);
        Interlocked.Increment(ref _transferRequestCount);

        if (success)
        {
            _transferSuccesses.Add(1, tags);
            Interlocked.Increment(ref _transferSuccessCount);
        }
        else
        {
            _transferFailures.Add(1, tags);
            Interlocked.Increment(ref _transferFailureCount);
        }
    }

    public void RecordFundsMutation(string mutationKind, bool success)
    {
        TagList tags = new()
        {
            { "mutation.kind", mutationKind },
            { "success", success }
        };

        _fundMutations.Add(1, tags);
        Interlocked.Increment(ref _fundMutationCount);

        if (!success)
        {
            _fundMutationFailures.Add(1, tags);
            Interlocked.Increment(ref _fundMutationFailureCount);
        }
    }

    public void RecordDispute(bool success)
    {
        TagList tags = new()
        {
            { "success", success }
        };

        if (success)
        {
            _disputesCreated.Add(1, tags);
            Interlocked.Increment(ref _disputesCreatedCount);
        }
        else
        {
            _disputeFailures.Add(1, tags);
            Interlocked.Increment(ref _disputeFailureCount);
        }
    }

    public void RecordSettlementRun(int processedTransfers, bool success)
    {
        TagList tags = new()
        {
            { "success", success }
        };

        _settlementRuns.Add(1, tags);
        Interlocked.Increment(ref _settlementRunCount);

        if (success)
        {
            _settlementSuccesses.Add(1, tags);
            Interlocked.Increment(ref _settlementSuccessCount);
        }
        else
        {
            _settlementFailures.Add(1, tags);
            Interlocked.Increment(ref _settlementFailureCount);
        }

        if (processedTransfers > 0)
        {
            _settlementTransfersProcessed.Add(processedTransfers, tags);
            Interlocked.Add(ref _settlementTransfersProcessedCount, processedTransfers);
        }
    }

    public void RecordBackgroundWorkerFailure()
    {
        _backgroundWorkerFailures.Add(1);
        Interlocked.Increment(ref _backgroundWorkerFailureCount);
    }

    public string BuildPrometheusSnapshot()
    {
        StringBuilder builder = new();

        AppendMetric(builder, "consoleatm_http_requests_total", "Total HTTP requests handled.", Volatile.Read(ref _httpRequestCount));
        AppendMetric(builder, "consoleatm_http_request_errors_total", "Total HTTP requests with 5xx responses.", Volatile.Read(ref _httpErrorCount));
        AppendMetric(builder, "consoleatm_http_request_duration_ms_count", "HTTP request duration sample count.", Volatile.Read(ref _httpDurationSamples));
        AppendMetric(builder, "consoleatm_http_request_duration_ms_sum", "HTTP request duration sum in milliseconds.", Volatile.Read(ref _httpDurationMsTotal));
        AppendMetric(builder, "consoleatm_transfer_requests_total", "Total transfer requests.", Volatile.Read(ref _transferRequestCount));
        AppendMetric(builder, "consoleatm_transfer_success_total", "Successful transfers.", Volatile.Read(ref _transferSuccessCount));
        AppendMetric(builder, "consoleatm_transfer_failures_total", "Failed transfers.", Volatile.Read(ref _transferFailureCount));
        AppendMetric(builder, "consoleatm_fund_mutations_total", "Total fund mutations.", Volatile.Read(ref _fundMutationCount));
        AppendMetric(builder, "consoleatm_fund_mutation_failures_total", "Failed fund mutations.", Volatile.Read(ref _fundMutationFailureCount));
        AppendMetric(builder, "consoleatm_disputes_created_total", "Disputes created successfully.", Volatile.Read(ref _disputesCreatedCount));
        AppendMetric(builder, "consoleatm_dispute_failures_total", "Failed dispute create/update actions.", Volatile.Read(ref _disputeFailureCount));
        AppendMetric(builder, "consoleatm_settlement_runs_total", "Settlement worker runs.", Volatile.Read(ref _settlementRunCount));
        AppendMetric(builder, "consoleatm_settlement_success_total", "Successful settlement worker runs.", Volatile.Read(ref _settlementSuccessCount));
        AppendMetric(builder, "consoleatm_settlement_failures_total", "Failed settlement worker runs.", Volatile.Read(ref _settlementFailureCount));
        AppendMetric(builder, "consoleatm_settlement_transfers_processed_total", "Transfers processed by settlement worker.", Volatile.Read(ref _settlementTransfersProcessedCount));
        AppendMetric(builder, "consoleatm_background_worker_failures_total", "Background worker failures.", Volatile.Read(ref _backgroundWorkerFailureCount));

        return builder.ToString();
    }

    public void Dispose()
    {
        ActivitySource.Dispose();
        Meter.Dispose();
    }

    private static void AppendMetric(StringBuilder builder, string name, string help, long value)
    {
        builder.Append("# HELP ").Append(name).Append(' ').Append(help).AppendLine();
        builder.Append("# TYPE ").Append(name).AppendLine(" counter");
        builder.Append(name).Append(' ').Append(value).AppendLine();
    }
}
