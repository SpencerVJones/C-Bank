namespace AtmMachine.Services.Models;

public sealed record TransferResult(
    bool IsSuccess,
    string Message,
    decimal SourceBalance,
    string? DestinationMaskedCard)
{
    public static TransferResult Success(
        string message,
        decimal sourceBalance,
        string destinationMaskedCard) =>
        new(true, message, sourceBalance, destinationMaskedCard);

    public static TransferResult Failure(string message, decimal sourceBalance) =>
        new(false, message, sourceBalance, null);
}
