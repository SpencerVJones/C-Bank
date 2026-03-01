using AtmMachine.Domain.Models;
using AtmMachine.Services.Abstractions;

namespace AtmMachine.Services;

public static class SampleAccountFactory
{
    private sealed record SeedAccount(
        string FirstName,
        string LastName,
        string CardNumber,
        string Pin,
        decimal OpeningBalance);

    private static readonly IReadOnlyList<SeedAccount> SeedAccounts = new List<SeedAccount>
    {
        new("Spencer", "Jones", "1234567890123456", "1234", 10234.43m),
        new("John", "Smith", "7890123456789012", "2345", 4534.32m),
        new("Alice", "Cooper", "3456789012345678", "6789", 832974.54m),
        new("Alex", "Hicks", "5678901234567890", "2340", 32.23m),
        new("Drake", "Wilson", "9012345678901234", "6780", 423.53m)
    };

    public static IReadOnlyList<Account> CreateDefaultAccounts(IPinHasher pinHasher, IClock clock)
    {
        if (pinHasher is null)
        {
            throw new ArgumentNullException(nameof(pinHasher));
        }

        if (clock is null)
        {
            throw new ArgumentNullException(nameof(clock));
        }

        DateTimeOffset now = clock.UtcNow;
        return SeedAccounts.Select(seed =>
        {
            (string hash, string salt) = pinHasher.CreateHash(seed.Pin);
            return new Account
            {
                FirstName = seed.FirstName,
                LastName = seed.LastName,
                CardNumber = seed.CardNumber,
                PinHash = hash,
                PinSalt = salt,
                Balance = seed.OpeningBalance,
                Transactions = new List<TransactionRecord>
                {
                    new()
                    {
                        TimestampUtc = now,
                        Type = TransactionType.Deposit,
                        Amount = seed.OpeningBalance,
                        BalanceAfter = seed.OpeningBalance,
                        Description = "Opening balance"
                    }
                }
            };
        }).ToList();
    }
}
