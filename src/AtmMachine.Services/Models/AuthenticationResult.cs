using AtmMachine.Domain.Models;

namespace AtmMachine.Services.Models;

public sealed record AuthenticationResult(
    bool IsSuccess,
    string Message,
    Account? Account,
    int RemainingAttempts,
    DateTimeOffset? LockedUntilUtc
)
{
    public static AuthenticationResult Success(Account account) =>
        new(true, "Authentication successful.", account, 0, null);

    public static AuthenticationResult Failure(string message, int remainingAttempts = 0) =>
        new(false, message, null, remainingAttempts, null);

    public static AuthenticationResult Locked(DateTimeOffset lockedUntilUtc) =>
        new(false, "Account is temporarily locked.", null, 0, lockedUntilUtc);
}
