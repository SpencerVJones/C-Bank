namespace AtmMachine.Domain.Models;

public sealed class Account
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string CardNumber { get; set; } = string.Empty;
    public string PinHash { get; set; } = string.Empty;
    public string PinSalt { get; set; } = string.Empty;
    public decimal Balance { get; set; }
    public int FailedPinAttempts { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }
    public List<TransactionRecord> Transactions { get; set; } = new();
}
