using AtmMachine.Domain.Models;

namespace AtmMachine.Services.Models;

public sealed record TransactionResult(
    bool IsSuccess,
    string Message,
    decimal CurrentBalance,
    TransactionRecord? Transaction
)
{
    public static TransactionResult Success(string message, decimal currentBalance, TransactionRecord transaction) =>
        new(true, message, currentBalance, transaction);

    public static TransactionResult Failure(string message, decimal currentBalance) =>
        new(false, message, currentBalance, null);
}
