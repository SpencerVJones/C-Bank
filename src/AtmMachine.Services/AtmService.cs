using AtmMachine.Domain.Abstractions;
using AtmMachine.Domain.Models;
using AtmMachine.Services.Abstractions;
using AtmMachine.Services.Models;

namespace AtmMachine.Services;

public sealed class AtmService
{
    private readonly IAccountRepository _accountRepository;
    private readonly IPinHasher _pinHasher;
    private readonly IClock _clock;
    private readonly AtmSecurityOptions _securityOptions;

    public AtmService(
        IAccountRepository accountRepository,
        IPinHasher pinHasher,
        IClock clock,
        AtmSecurityOptions? securityOptions = null)
    {
        _accountRepository = accountRepository ?? throw new ArgumentNullException(nameof(accountRepository));
        _pinHasher = pinHasher ?? throw new ArgumentNullException(nameof(pinHasher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _securityOptions = securityOptions ?? new AtmSecurityOptions();
    }

    public async Task<AuthenticationResult> AuthenticateAsync(
        string cardNumber,
        string pin,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidCardNumber(cardNumber))
        {
            return AuthenticationResult.Failure("Card number must be exactly 16 digits.");
        }

        if (!IsValidPin(pin))
        {
            return AuthenticationResult.Failure("PIN must be exactly 4 digits.");
        }

        Account? account = await _accountRepository.GetByCardNumberAsync(cardNumber, cancellationToken);
        if (account is null)
        {
            return AuthenticationResult.Failure("Invalid card number or PIN.");
        }

        if (account.LockedUntilUtc.HasValue && account.LockedUntilUtc.Value > _clock.UtcNow)
        {
            return AuthenticationResult.Locked(account.LockedUntilUtc.Value);
        }

        bool isValidPin = _pinHasher.Verify(pin, account.PinSalt, account.PinHash);
        if (isValidPin)
        {
            account.FailedPinAttempts = 0;
            account.LockedUntilUtc = null;
            await _accountRepository.SaveAccountAsync(account, cancellationToken);
            return AuthenticationResult.Success(account);
        }

        account.FailedPinAttempts++;
        int remainingAttempts = Math.Max(0, _securityOptions.MaxFailedPinAttempts - account.FailedPinAttempts);
        if (remainingAttempts == 0)
        {
            account.LockedUntilUtc = _clock.UtcNow.Add(_securityOptions.LockoutDuration);
            await _accountRepository.SaveAccountAsync(account, cancellationToken);
            return AuthenticationResult.Locked(account.LockedUntilUtc.Value);
        }

        await _accountRepository.SaveAccountAsync(account, cancellationToken);
        return AuthenticationResult.Failure(
            $"Invalid card number or PIN. {remainingAttempts} attempt(s) remaining.",
            remainingAttempts);
    }

    public async Task<TransactionResult> DepositAsync(
        Account account,
        decimal amount,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        if (account is null)
        {
            throw new ArgumentNullException(nameof(account));
        }

        if (!TryNormalizeAmount(amount, out decimal normalizedAmount))
        {
            return TransactionResult.Failure("Deposit amount must be greater than 0.00.", account.Balance);
        }

        account.Balance = decimal.Round(account.Balance + normalizedAmount, 2, MidpointRounding.AwayFromZero);
        TransactionRecord transaction = BuildTransaction(
            TransactionType.Deposit,
            normalizedAmount,
            account.Balance,
            description ?? "Cash deposit");

        account.Transactions.Add(transaction);
        await _accountRepository.SaveAccountAsync(account, cancellationToken);

        return TransactionResult.Success("Deposit completed.", account.Balance, transaction);
    }

    public async Task<TransactionResult> WithdrawAsync(
        Account account,
        decimal amount,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        if (account is null)
        {
            throw new ArgumentNullException(nameof(account));
        }

        if (!TryNormalizeAmount(amount, out decimal normalizedAmount))
        {
            return TransactionResult.Failure("Withdrawal amount must be greater than 0.00.", account.Balance);
        }

        if (normalizedAmount > account.Balance)
        {
            return TransactionResult.Failure("Insufficient balance.", account.Balance);
        }

        account.Balance = decimal.Round(account.Balance - normalizedAmount, 2, MidpointRounding.AwayFromZero);
        TransactionRecord transaction = BuildTransaction(
            TransactionType.Withdrawal,
            normalizedAmount,
            account.Balance,
            description ?? "Cash withdrawal");

        account.Transactions.Add(transaction);
        await _accountRepository.SaveAccountAsync(account, cancellationToken);

        return TransactionResult.Success("Withdrawal completed.", account.Balance, transaction);
    }

    public async Task<TransferResult> TransferAsync(
        Account sourceAccount,
        string destinationCardNumber,
        decimal amount,
        string? memo = null,
        CancellationToken cancellationToken = default)
    {
        if (sourceAccount is null)
        {
            throw new ArgumentNullException(nameof(sourceAccount));
        }

        if (!IsValidCardNumber(destinationCardNumber))
        {
            return TransferResult.Failure("Destination card number must be exactly 16 digits.", sourceAccount.Balance);
        }

        if (sourceAccount.CardNumber == destinationCardNumber)
        {
            return TransferResult.Failure("Cannot transfer to the same account.", sourceAccount.Balance);
        }

        if (!TryNormalizeAmount(amount, out decimal normalizedAmount))
        {
            return TransferResult.Failure("Transfer amount must be greater than 0.00.", sourceAccount.Balance);
        }

        if (normalizedAmount > sourceAccount.Balance)
        {
            return TransferResult.Failure("Insufficient balance for transfer.", sourceAccount.Balance);
        }

        Account? destinationAccount =
            await _accountRepository.GetByCardNumberAsync(destinationCardNumber, cancellationToken);
        if (destinationAccount is null)
        {
            return TransferResult.Failure("Destination account not found.", sourceAccount.Balance);
        }

        sourceAccount.Balance = decimal.Round(
            sourceAccount.Balance - normalizedAmount,
            2,
            MidpointRounding.AwayFromZero);
        destinationAccount.Balance = decimal.Round(
            destinationAccount.Balance + normalizedAmount,
            2,
            MidpointRounding.AwayFromZero);

        string memoText = string.IsNullOrWhiteSpace(memo) ? string.Empty : $" ({memo.Trim()})";
        string destinationMasked = MaskCard(destinationAccount.CardNumber);
        string sourceMasked = MaskCard(sourceAccount.CardNumber);

        sourceAccount.Transactions.Add(BuildTransaction(
            TransactionType.Withdrawal,
            normalizedAmount,
            sourceAccount.Balance,
            $"Transfer to {destinationMasked}{memoText}"));
        destinationAccount.Transactions.Add(BuildTransaction(
            TransactionType.Deposit,
            normalizedAmount,
            destinationAccount.Balance,
            $"Transfer from {sourceMasked}{memoText}"));

        await _accountRepository.SaveAccountAsync(sourceAccount, cancellationToken);
        await _accountRepository.SaveAccountAsync(destinationAccount, cancellationToken);

        return TransferResult.Success(
            "Transfer completed.",
            sourceAccount.Balance,
            destinationMasked);
    }

    public IReadOnlyList<TransactionRecord> GetStatement(Account account, int count = 10)
    {
        if (account is null)
        {
            throw new ArgumentNullException(nameof(account));
        }

        if (count <= 0)
        {
            return Array.Empty<TransactionRecord>();
        }

        return account.Transactions
            .OrderByDescending(transaction => transaction.TimestampUtc)
            .Take(count)
            .ToList();
    }

    private TransactionRecord BuildTransaction(
        TransactionType type,
        decimal amount,
        decimal balanceAfter,
        string description)
    {
        return new TransactionRecord
        {
            TimestampUtc = _clock.UtcNow,
            Type = type,
            Amount = amount,
            BalanceAfter = balanceAfter,
            Description = description
        };
    }

    private static bool IsValidCardNumber(string cardNumber) =>
        cardNumber.Length == 16 && cardNumber.All(char.IsDigit);

    private static bool IsValidPin(string pin) =>
        pin.Length == 4 && pin.All(char.IsDigit);

    private static bool TryNormalizeAmount(decimal amount, out decimal normalizedAmount)
    {
        normalizedAmount = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
        return normalizedAmount > 0m;
    }

    private static string MaskCard(string cardNumber)
    {
        if (string.IsNullOrWhiteSpace(cardNumber) || cardNumber.Length <= 4)
        {
            return "****";
        }

        return $"{new string('*', cardNumber.Length - 4)}{cardNumber[^4..]}";
    }
}
