using System.Text.Json;
using AtmMachine.Domain.Abstractions;
using AtmMachine.Domain.Models;

namespace AtmMachine.Data;

public sealed class JsonAccountRepository : IAccountRepository
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public JsonAccountRepository(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));
        }

        _filePath = Path.GetFullPath(filePath);
        string? directoryPath = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }
    }

    public async Task<bool> AnyAccountsAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            List<Account> accounts = await LoadAccountsUnsafeAsync(cancellationToken);
            return accounts.Count > 0;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<IReadOnlyList<Account>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            List<Account> accounts = await LoadAccountsUnsafeAsync(cancellationToken);
            return accounts;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<Account?> GetByCardNumberAsync(string cardNumber, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cardNumber))
        {
            return null;
        }

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            List<Account> accounts = await LoadAccountsUnsafeAsync(cancellationToken);
            return accounts.FirstOrDefault(account => account.CardNumber == cardNumber);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task SaveAccountAsync(Account account, CancellationToken cancellationToken = default)
    {
        if (account is null)
        {
            throw new ArgumentNullException(nameof(account));
        }

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            List<Account> accounts = await LoadAccountsUnsafeAsync(cancellationToken);
            int index = accounts.FindIndex(existing =>
                existing.Id == account.Id || existing.CardNumber == account.CardNumber);

            if (index >= 0)
            {
                accounts[index] = account;
            }
            else
            {
                accounts.Add(account);
            }

            await SaveAccountsUnsafeAsync(accounts, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task SeedAsync(IEnumerable<Account> accounts, CancellationToken cancellationToken = default)
    {
        if (accounts is null)
        {
            throw new ArgumentNullException(nameof(accounts));
        }

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            List<Account> existingAccounts = await LoadAccountsUnsafeAsync(cancellationToken);
            if (existingAccounts.Count > 0)
            {
                return;
            }

            List<Account> seedAccounts = accounts.ToList();
            await SaveAccountsUnsafeAsync(seedAccounts, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<List<Account>> LoadAccountsUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new List<Account>();
        }

        await using FileStream stream = File.OpenRead(_filePath);
        if (stream.Length == 0)
        {
            return new List<Account>();
        }

        List<Account>? accounts =
            await JsonSerializer.DeserializeAsync<List<Account>>(stream, _jsonOptions, cancellationToken);

        if (accounts is null)
        {
            return new List<Account>();
        }

        foreach (Account account in accounts)
        {
            account.Transactions ??= new List<TransactionRecord>();
        }

        return accounts;
    }

    private async Task SaveAccountsUnsafeAsync(List<Account> accounts, CancellationToken cancellationToken)
    {
        string tempPath = $"{_filePath}.tmp";
        await using (FileStream stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, accounts, _jsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(tempPath, _filePath, overwrite: true);
    }
}
