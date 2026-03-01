using System.Text.Json;
using AtmMachine.WebUI.Banking.Models;

namespace AtmMachine.WebUI.Banking.Infrastructure;

public interface IBankingDataStore
{
    Task<T> ReadAsync<T>(
        Func<BankDatabase, T> query,
        CancellationToken cancellationToken = default);

    Task<T> WriteAsync<T>(
        Func<BankDatabase, T> mutate,
        CancellationToken cancellationToken = default);

    Task WriteAsync(
        Action<BankDatabase> mutate,
        CancellationToken cancellationToken = default);
}

public sealed class BankingDataStore : IBankingDataStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public BankingDataStore(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Data file path is required.", nameof(filePath));
        }

        _filePath = Path.GetFullPath(filePath);
        string? directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public async Task<T> ReadAsync<T>(
        Func<BankDatabase, T> query,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            BankDatabase database = await LoadUnsafeAsync(cancellationToken);
            return query(database);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<T> WriteAsync<T>(
        Func<BankDatabase, T> mutate,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            BankDatabase database = await LoadUnsafeAsync(cancellationToken);
            T result = mutate(database);
            await SaveUnsafeAsync(database, cancellationToken);
            return result;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task WriteAsync(
        Action<BankDatabase> mutate,
        CancellationToken cancellationToken = default)
    {
        await WriteAsync(database =>
        {
            mutate(database);
            return true;
        }, cancellationToken);
    }

    private async Task<BankDatabase> LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new BankDatabase();
        }

        await using FileStream file = File.OpenRead(_filePath);
        if (file.Length == 0)
        {
            return new BankDatabase();
        }

        BankDatabase? database = await JsonSerializer.DeserializeAsync<BankDatabase>(
            file,
            _jsonOptions,
            cancellationToken);

        return database ?? new BankDatabase();
    }

    private async Task SaveUnsafeAsync(BankDatabase database, CancellationToken cancellationToken)
    {
        string tempPath = $"{_filePath}.tmp";
        await using (FileStream stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, database, _jsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        File.Move(tempPath, _filePath, overwrite: true);
    }
}
