using AtmMachine.Domain.Abstractions;
using AtmMachine.Domain.Models;
using AtmMachine.Services;
using AtmMachine.Services.Abstractions;
using AtmMachine.Services.Models;

namespace AtmMachine.Tests;

public sealed class AtmServiceTests
{
    [Fact]
    public async Task AuthenticateAsync_ValidCredentials_ReturnsSuccessAndResetsSecurityState()
    {
        FakeClock clock = new(new DateTimeOffset(2026, 2, 19, 12, 0, 0, TimeSpan.Zero));
        Pbkdf2PinHasher pinHasher = new();
        Account account = CreateAccount(pinHasher, "1111222233334444", "1234", 100m);
        account.FailedPinAttempts = 2;
        account.LockedUntilUtc = clock.UtcNow.AddMinutes(-1);

        InMemoryAccountRepository repository = new(new[] { account });
        AtmService service = CreateService(repository, pinHasher, clock);

        AuthenticationResult result = await service.AuthenticateAsync("1111222233334444", "1234");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Account);
        Assert.Equal(0, account.FailedPinAttempts);
        Assert.Null(account.LockedUntilUtc);
    }

    [Fact]
    public async Task AuthenticateAsync_InvalidCardFormat_ReturnsFailure()
    {
        FakeClock clock = new(new DateTimeOffset(2026, 2, 19, 12, 0, 0, TimeSpan.Zero));
        Pbkdf2PinHasher pinHasher = new();
        Account account = CreateAccount(pinHasher, "1111222233334444", "1234", 100m);
        InMemoryAccountRepository repository = new(new[] { account });
        AtmService service = CreateService(repository, pinHasher, clock);

        AuthenticationResult result = await service.AuthenticateAsync("1111", "1234");

        Assert.False(result.IsSuccess);
        Assert.Null(result.Account);
        Assert.Equal(0, account.FailedPinAttempts);
    }

    [Fact]
    public async Task AuthenticateAsync_ReachesAttemptLimit_LocksAccount()
    {
        FakeClock clock = new(new DateTimeOffset(2026, 2, 19, 12, 0, 0, TimeSpan.Zero));
        Pbkdf2PinHasher pinHasher = new();
        Account account = CreateAccount(pinHasher, "9999888877776666", "1234", 300m);
        InMemoryAccountRepository repository = new(new[] { account });
        AtmService service = CreateService(
            repository,
            pinHasher,
            clock,
            new AtmSecurityOptions
            {
                MaxFailedPinAttempts = 2,
                LockoutDuration = TimeSpan.FromMinutes(15)
            });

        AuthenticationResult firstAttempt = await service.AuthenticateAsync("9999888877776666", "0000");
        AuthenticationResult secondAttempt = await service.AuthenticateAsync("9999888877776666", "0000");

        Assert.False(firstAttempt.IsSuccess);
        Assert.False(secondAttempt.IsSuccess);
        Assert.NotNull(secondAttempt.LockedUntilUtc);
        Assert.Equal(clock.UtcNow.AddMinutes(15), secondAttempt.LockedUntilUtc);
        Assert.Equal(2, account.FailedPinAttempts);
    }

    [Fact]
    public async Task DepositAsync_ValidAmount_UpdatesBalanceAndAddsTransaction()
    {
        FakeClock clock = new(new DateTimeOffset(2026, 2, 19, 12, 0, 0, TimeSpan.Zero));
        Pbkdf2PinHasher pinHasher = new();
        Account account = CreateAccount(pinHasher, "1212121212121212", "1234", 50m);
        InMemoryAccountRepository repository = new(new[] { account });
        AtmService service = CreateService(repository, pinHasher, clock);

        var result = await service.DepositAsync(account, 25.55m);

        Assert.True(result.IsSuccess);
        Assert.Equal(75.55m, result.CurrentBalance);
        Assert.Single(account.Transactions);
        Assert.Equal(TransactionType.Deposit, account.Transactions[0].Type);
        Assert.Equal(25.55m, account.Transactions[0].Amount);
    }

    [Fact]
    public async Task DepositAsync_NonPositiveAmount_DoesNotChangeBalance()
    {
        FakeClock clock = new(new DateTimeOffset(2026, 2, 19, 12, 0, 0, TimeSpan.Zero));
        Pbkdf2PinHasher pinHasher = new();
        Account account = CreateAccount(pinHasher, "2323232323232323", "1234", 20m);
        InMemoryAccountRepository repository = new(new[] { account });
        AtmService service = CreateService(repository, pinHasher, clock);

        var result = await service.DepositAsync(account, 0m);

        Assert.False(result.IsSuccess);
        Assert.Equal(20m, result.CurrentBalance);
        Assert.Empty(account.Transactions);
    }

    [Fact]
    public async Task WithdrawAsync_InsufficientFunds_DoesNotChangeBalance()
    {
        FakeClock clock = new(new DateTimeOffset(2026, 2, 19, 12, 0, 0, TimeSpan.Zero));
        Pbkdf2PinHasher pinHasher = new();
        Account account = CreateAccount(pinHasher, "3434343434343434", "1234", 20m);
        InMemoryAccountRepository repository = new(new[] { account });
        AtmService service = CreateService(repository, pinHasher, clock);

        var result = await service.WithdrawAsync(account, 50m);

        Assert.False(result.IsSuccess);
        Assert.Equal(20m, result.CurrentBalance);
        Assert.Empty(account.Transactions);
    }

    [Fact]
    public async Task WithdrawAsync_ValidAmount_UpdatesBalanceAndAddsTransaction()
    {
        FakeClock clock = new(new DateTimeOffset(2026, 2, 19, 12, 0, 0, TimeSpan.Zero));
        Pbkdf2PinHasher pinHasher = new();
        Account account = CreateAccount(pinHasher, "5656565656565656", "1234", 80m);
        InMemoryAccountRepository repository = new(new[] { account });
        AtmService service = CreateService(repository, pinHasher, clock);

        var result = await service.WithdrawAsync(account, 30m);

        Assert.True(result.IsSuccess);
        Assert.Equal(50m, result.CurrentBalance);
        Assert.Single(account.Transactions);
        Assert.Equal(TransactionType.Withdrawal, account.Transactions[0].Type);
        Assert.Equal(30m, account.Transactions[0].Amount);
    }

    [Fact]
    public async Task TransferAsync_ValidTransfer_UpdatesBothAccounts()
    {
        FakeClock clock = new(new DateTimeOffset(2026, 2, 19, 12, 0, 0, TimeSpan.Zero));
        Pbkdf2PinHasher pinHasher = new();
        Account sourceAccount = CreateAccount(pinHasher, "1111222233334444", "1234", 500m);
        Account destinationAccount = CreateAccount(pinHasher, "9999000011112222", "1234", 125m);

        InMemoryAccountRepository repository = new(new[] { sourceAccount, destinationAccount });
        AtmService service = CreateService(repository, pinHasher, clock);

        var result = await service.TransferAsync(sourceAccount, "9999000011112222", 50m, "Rent split");

        Assert.True(result.IsSuccess);
        Assert.Equal(450m, sourceAccount.Balance);
        Assert.Equal(175m, destinationAccount.Balance);
        Assert.Single(sourceAccount.Transactions);
        Assert.Single(destinationAccount.Transactions);
        Assert.Equal(TransactionType.Withdrawal, sourceAccount.Transactions[0].Type);
        Assert.Equal(TransactionType.Deposit, destinationAccount.Transactions[0].Type);
    }

    [Fact]
    public async Task TransferAsync_UnknownDestination_ReturnsFailure()
    {
        FakeClock clock = new(new DateTimeOffset(2026, 2, 19, 12, 0, 0, TimeSpan.Zero));
        Pbkdf2PinHasher pinHasher = new();
        Account sourceAccount = CreateAccount(pinHasher, "1111222233334444", "1234", 500m);
        InMemoryAccountRepository repository = new(new[] { sourceAccount });
        AtmService service = CreateService(repository, pinHasher, clock);

        var result = await service.TransferAsync(sourceAccount, "1234123412341234", 25m);

        Assert.False(result.IsSuccess);
        Assert.Equal(500m, sourceAccount.Balance);
        Assert.Empty(sourceAccount.Transactions);
    }

    [Fact]
    public async Task TransferAsync_InsufficientFunds_ReturnsFailure()
    {
        FakeClock clock = new(new DateTimeOffset(2026, 2, 19, 12, 0, 0, TimeSpan.Zero));
        Pbkdf2PinHasher pinHasher = new();
        Account sourceAccount = CreateAccount(pinHasher, "1111222233334444", "1234", 40m);
        Account destinationAccount = CreateAccount(pinHasher, "9999000011112222", "1234", 125m);

        InMemoryAccountRepository repository = new(new[] { sourceAccount, destinationAccount });
        AtmService service = CreateService(repository, pinHasher, clock);

        var result = await service.TransferAsync(sourceAccount, "9999000011112222", 90m);

        Assert.False(result.IsSuccess);
        Assert.Equal(40m, sourceAccount.Balance);
        Assert.Equal(125m, destinationAccount.Balance);
        Assert.Empty(sourceAccount.Transactions);
        Assert.Empty(destinationAccount.Transactions);
    }

    private static AtmService CreateService(
        IAccountRepository repository,
        IPinHasher pinHasher,
        IClock clock,
        AtmSecurityOptions? options = null) =>
        new(repository, pinHasher, clock, options);

    private static Account CreateAccount(IPinHasher pinHasher, string cardNumber, string pin, decimal balance)
    {
        (string hash, string salt) = pinHasher.CreateHash(pin);
        return new Account
        {
            FirstName = "Test",
            LastName = "User",
            CardNumber = cardNumber,
            PinHash = hash,
            PinSalt = salt,
            Balance = balance
        };
    }

    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTimeOffset utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTimeOffset UtcNow { get; set; }
    }

    private sealed class InMemoryAccountRepository : IAccountRepository
    {
        private readonly List<Account> _accounts;

        public InMemoryAccountRepository(IEnumerable<Account> accounts)
        {
            _accounts = accounts.ToList();
        }

        public Task<bool> AnyAccountsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_accounts.Count > 0);

        public Task<IReadOnlyList<Account>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Account>>(_accounts);

        public Task<Account?> GetByCardNumberAsync(string cardNumber, CancellationToken cancellationToken = default)
        {
            Account? account = _accounts.FirstOrDefault(existing => existing.CardNumber == cardNumber);
            return Task.FromResult(account);
        }

        public Task SaveAccountAsync(Account account, CancellationToken cancellationToken = default)
        {
            int index = _accounts.FindIndex(existing =>
                existing.Id == account.Id || existing.CardNumber == account.CardNumber);

            if (index >= 0)
            {
                _accounts[index] = account;
            }
            else
            {
                _accounts.Add(account);
            }

            return Task.CompletedTask;
        }

        public Task SeedAsync(IEnumerable<Account> accounts, CancellationToken cancellationToken = default)
        {
            if (_accounts.Count == 0)
            {
                _accounts.AddRange(accounts);
            }

            return Task.CompletedTask;
        }
    }
}
