namespace AtmMachine.Domain.Models;

public sealed class TransactionRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset TimestampUtc { get; set; }
    public TransactionType Type { get; set; }
    public decimal Amount { get; set; }
    public decimal BalanceAfter { get; set; }
    public string Description { get; set; } = string.Empty;
}
