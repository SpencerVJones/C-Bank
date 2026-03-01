using AtmMachine.Domain.Models;

namespace AtmMachine.Domain.Abstractions;

public interface IAccountRepository
{
    Task<bool> AnyAccountsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Account>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<Account?> GetByCardNumberAsync(string cardNumber, CancellationToken cancellationToken = default);
    Task SaveAccountAsync(Account account, CancellationToken cancellationToken = default);
    Task SeedAsync(IEnumerable<Account> accounts, CancellationToken cancellationToken = default);
}
