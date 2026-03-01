using System.Diagnostics;
using System.Text.Json;
using AtmMachine.WebUI.Banking.Models;

namespace AtmMachine.WebUI.Banking.Infrastructure;

public sealed class SqliteBankingDataStore : IBankingDataStore
{
    private readonly string _dbPath;
    private readonly string _sqliteExecutable;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public SqliteBankingDataStore(string dbPath, string sqliteExecutable = "sqlite3")
    {
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            throw new ArgumentException("Database path is required.", nameof(dbPath));
        }

        _dbPath = Path.GetFullPath(dbPath);
        _sqliteExecutable = string.IsNullOrWhiteSpace(sqliteExecutable)
            ? "sqlite3"
            : sqliteExecutable.Trim();

        if (Path.IsPathRooted(_sqliteExecutable) && !File.Exists(_sqliteExecutable))
        {
            throw new FileNotFoundException($"SQLite executable was not found at '{_sqliteExecutable}'.");
        }

        string? directory = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        InitializeSchema();
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

    private void InitializeSchema()
    {
        const string schemaSql = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS bank_state (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                payload TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            """;

        ExecuteSqlAsync(schemaSql, CancellationToken.None).GetAwaiter().GetResult();
    }

    private async Task<BankDatabase> LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        string output = await ExecuteSqlAsync(
            "SELECT payload FROM bank_state WHERE id = 1;",
            cancellationToken);

        string payload = output.Trim();
        if (string.IsNullOrWhiteSpace(payload))
        {
            return new BankDatabase();
        }

        BankDatabase? database = JsonSerializer.Deserialize<BankDatabase>(payload, _jsonOptions);
        return database ?? new BankDatabase();
    }

    private async Task SaveUnsafeAsync(BankDatabase database, CancellationToken cancellationToken)
    {
        string payload = JsonSerializer.Serialize(database, _jsonOptions);
        string escapedPayload = payload.Replace("'", "''");
        string updatedUtc = DateTimeOffset.UtcNow.ToString("O");

        string sql = $"""
            BEGIN IMMEDIATE;
            INSERT INTO bank_state (id, payload, updated_utc)
            VALUES (1, '{escapedPayload}', '{updatedUtc}')
            ON CONFLICT(id) DO UPDATE SET
                payload = excluded.payload,
                updated_utc = excluded.updated_utc;
            COMMIT;
            """;

        await ExecuteSqlAsync(sql, cancellationToken);
    }

    private async Task<string> ExecuteSqlAsync(string sql, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = _sqliteExecutable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-batch");
        startInfo.ArgumentList.Add("-noheader");
        startInfo.ArgumentList.Add(_dbPath);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start sqlite3 process.");

        await process.StandardInput.WriteAsync(sql.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        process.StandardInput.Close();

        string stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        string stderr = await process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore best-effort kill exceptions for cancellation flow.
            }

            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"SQLite command failed with exit code {process.ExitCode}: {stderr}");
        }

        return stdout;
    }
}
