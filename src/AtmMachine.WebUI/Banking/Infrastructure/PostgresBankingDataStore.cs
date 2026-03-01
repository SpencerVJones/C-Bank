using System.Data;
using System.Text.Json;
using AtmMachine.WebUI.Banking.Models;
using Npgsql;

namespace AtmMachine.WebUI.Banking.Infrastructure;

public sealed class PostgresBankingDataStore : IBankingDataStore
{
    private readonly string _connectionString;
    private readonly string _stateKey;
    private readonly SemaphoreSlim _initializeMutex = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };
    private volatile bool _schemaReady;

    public PostgresBankingDataStore(PostgresBankingStoreOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        _connectionString = Require(options.ConnectionString, nameof(options.ConnectionString));
        _stateKey = string.IsNullOrWhiteSpace(options.StateKey)
            ? "bank_state"
            : options.StateKey.Trim();
    }

    public async Task<T> ReadAsync<T>(
        Func<BankDatabase, T> query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await EnsureSchemaAsync(cancellationToken);

        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);

        BankDatabase database = await LoadDatabaseAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return query(database);
    }

    public async Task<T> WriteAsync<T>(
        Func<BankDatabase, T> mutate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        await EnsureSchemaAsync(cancellationToken);

        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        try
        {
            await LockStateAsync(connection, transaction, cancellationToken);
            BankDatabase database = await LoadDatabaseAsync(connection, transaction, cancellationToken);
            T result = mutate(database);
            await SaveDatabaseAsync(connection, transaction, database, cancellationToken);
            await TouchStateAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
                // Ignore rollback failures; the original exception is more relevant.
            }

            throw;
        }
    }

    public async Task WriteAsync(
        Action<BankDatabase> mutate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        await WriteAsync(database =>
        {
            mutate(database);
            return true;
        }, cancellationToken);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (_schemaReady)
        {
            return;
        }

        await _initializeMutex.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady)
            {
                return;
            }

            await using NpgsqlConnection connection = new(_connectionString);
            await connection.OpenAsync(cancellationToken);

            const string sql = """
                CREATE TABLE IF NOT EXISTS bank_runtime_state (
                    state_key TEXT PRIMARY KEY,
                    updated_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
                );

                CREATE TABLE IF NOT EXISTS bank_users (
                    state_key TEXT NOT NULL,
                    user_id UUID NOT NULL,
                    email TEXT NOT NULL,
                    role INTEGER NOT NULL,
                    created_utc TIMESTAMPTZ NOT NULL,
                    payload JSONB NOT NULL,
                    PRIMARY KEY (state_key, user_id)
                );

                CREATE TABLE IF NOT EXISTS bank_accounts (
                    state_key TEXT NOT NULL,
                    account_id UUID NOT NULL,
                    user_id UUID NOT NULL,
                    account_type INTEGER NOT NULL,
                    status INTEGER NOT NULL,
                    is_system_account BOOLEAN NOT NULL DEFAULT FALSE,
                    created_utc TIMESTAMPTZ NOT NULL,
                    payload JSONB NOT NULL,
                    PRIMARY KEY (state_key, account_id)
                );

                CREATE TABLE IF NOT EXISTS bank_ledger_entries (
                    state_key TEXT NOT NULL,
                    entry_id UUID NOT NULL,
                    account_id UUID NOT NULL,
                    user_id UUID NOT NULL,
                    transfer_id UUID NULL,
                    created_utc TIMESTAMPTZ NOT NULL,
                    entry_type TEXT NOT NULL,
                    payload JSONB NOT NULL,
                    PRIMARY KEY (state_key, entry_id)
                );

                CREATE TABLE IF NOT EXISTS bank_transactions (
                    state_key TEXT NOT NULL,
                    transaction_id UUID NOT NULL,
                    user_id UUID NOT NULL,
                    account_id UUID NOT NULL,
                    transfer_id UUID NULL,
                    created_utc TIMESTAMPTZ NOT NULL,
                    posted_utc TIMESTAMPTZ NULL,
                    state INTEGER NOT NULL,
                    amount NUMERIC(18, 2) NOT NULL,
                    category TEXT NOT NULL,
                    payload JSONB NOT NULL,
                    PRIMARY KEY (state_key, transaction_id)
                );

                CREATE TABLE IF NOT EXISTS bank_transfers (
                    state_key TEXT NOT NULL,
                    transfer_id UUID NOT NULL,
                    user_id UUID NOT NULL,
                    source_account_id UUID NOT NULL,
                    destination_internal_account_id UUID NULL,
                    destination_external_account_id UUID NULL,
                    state INTEGER NOT NULL,
                    frequency INTEGER NOT NULL,
                    requested_utc TIMESTAMPTZ NOT NULL,
                    settles_utc TIMESTAMPTZ NULL,
                    fraud_score INTEGER NOT NULL DEFAULT 0,
                    payload JSONB NOT NULL,
                    PRIMARY KEY (state_key, transfer_id)
                );

                CREATE TABLE IF NOT EXISTS bank_disputes (
                    state_key TEXT NOT NULL,
                    dispute_id UUID NOT NULL,
                    user_id UUID NOT NULL,
                    transaction_id UUID NOT NULL,
                    status INTEGER NOT NULL,
                    created_utc TIMESTAMPTZ NOT NULL,
                    payload JSONB NOT NULL,
                    PRIMARY KEY (state_key, dispute_id)
                );

                CREATE TABLE IF NOT EXISTS bank_supporting_state (
                    state_key TEXT PRIMARY KEY,
                    payload JSONB NOT NULL DEFAULT '{}'::jsonb,
                    updated_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
                );

                CREATE INDEX IF NOT EXISTS idx_bank_users_state_role
                    ON bank_users (state_key, role);
                CREATE INDEX IF NOT EXISTS idx_bank_accounts_state_user
                    ON bank_accounts (state_key, user_id);
                CREATE INDEX IF NOT EXISTS idx_bank_ledger_entries_state_account
                    ON bank_ledger_entries (state_key, account_id, created_utc);
                CREATE INDEX IF NOT EXISTS idx_bank_transactions_state_account
                    ON bank_transactions (state_key, account_id, created_utc);
                CREATE INDEX IF NOT EXISTS idx_bank_transfers_state_user
                    ON bank_transfers (state_key, user_id, requested_utc);
                CREATE INDEX IF NOT EXISTS idx_bank_disputes_state_user
                    ON bank_disputes (state_key, user_id, created_utc);
                """;

            await using NpgsqlCommand command = new(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);

            _schemaReady = true;
        }
        finally
        {
            _initializeMutex.Release();
        }
    }

    private async Task LockStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string ensureSql = """
            INSERT INTO bank_runtime_state (state_key)
            VALUES (@stateKey)
            ON CONFLICT (state_key) DO NOTHING;
            """;

        await using (NpgsqlCommand ensure = new(ensureSql, connection, transaction))
        {
            ensure.Parameters.AddWithValue("stateKey", _stateKey);
            await ensure.ExecuteNonQueryAsync(cancellationToken);
        }

        const string lockSql = """
            SELECT state_key
            FROM bank_runtime_state
            WHERE state_key = @stateKey
            FOR UPDATE;
            """;

        await using NpgsqlCommand command = new(lockSql, connection, transaction);
        command.Parameters.AddWithValue("stateKey", _stateKey);

        object? result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null || result is DBNull)
        {
            throw new InvalidOperationException("Failed to acquire PostgreSQL banking state lock.");
        }
    }

    private async Task TouchStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE bank_runtime_state
            SET updated_utc = NOW()
            WHERE state_key = @stateKey;
            """;

        await using NpgsqlCommand command = new(sql, connection, transaction);
        command.Parameters.AddWithValue("stateKey", _stateKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<BankDatabase> LoadDatabaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        SupportingBankState supporting = await LoadSupportingStateAsync(connection, transaction, cancellationToken);

        BankDatabase database = new()
        {
            Users = await LoadUsersAsync(connection, transaction, cancellationToken),
            Accounts = await LoadAccountsAsync(connection, transaction, cancellationToken),
            LinkedExternalAccounts = supporting.LinkedExternalAccounts,
            Transactions = await LoadTransactionsAsync(connection, transaction, cancellationToken),
            LedgerEntries = await LoadLedgerEntriesAsync(connection, transaction, cancellationToken),
            Transfers = await LoadTransfersAsync(connection, transaction, cancellationToken),
            Budgets = supporting.Budgets,
            Goals = supporting.Goals,
            Notifications = supporting.Notifications,
            LoginHistory = supporting.LoginHistory,
            Devices = supporting.Devices,
            Disputes = await LoadDisputesAsync(connection, transaction, cancellationToken),
            AuditLogs = supporting.AuditLogs,
            Statements = supporting.Statements,
            IdempotencyKeys = supporting.IdempotencyKeys,
            DomainEvents = supporting.DomainEvents
        };

        if (HasPersistedRows(database))
        {
            return database;
        }

        BankDatabase? legacyDatabase = await TryLoadLegacyStateAsync(connection, transaction, cancellationToken);
        return legacyDatabase ?? database;
    }

    private async Task SaveDatabaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        BankDatabase database,
        CancellationToken cancellationToken)
    {
        await ReplaceUsersAsync(connection, transaction, database.Users, cancellationToken);
        await ReplaceAccountsAsync(connection, transaction, database.Accounts, cancellationToken);
        await ReplaceLedgerEntriesAsync(connection, transaction, database.LedgerEntries, cancellationToken);
        await ReplaceTransactionsAsync(connection, transaction, database.Transactions, cancellationToken);
        await ReplaceTransfersAsync(connection, transaction, database.Transfers, cancellationToken);
        await ReplaceDisputesAsync(connection, transaction, database.Disputes, cancellationToken);
        await SaveSupportingStateAsync(connection, transaction, database, cancellationToken);
    }

    private Task<List<BankUser>> LoadUsersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT payload::text
            FROM bank_users
            WHERE state_key = @stateKey
            ORDER BY created_utc, user_id;
            """;

        return LoadPayloadRowsAsync<BankUser>(connection, transaction, sql, cancellationToken);
    }

    private Task<List<BankAccount>> LoadAccountsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT payload::text
            FROM bank_accounts
            WHERE state_key = @stateKey
            ORDER BY created_utc, account_id;
            """;

        return LoadPayloadRowsAsync<BankAccount>(connection, transaction, sql, cancellationToken);
    }

    private Task<List<LedgerEntry>> LoadLedgerEntriesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT payload::text
            FROM bank_ledger_entries
            WHERE state_key = @stateKey
            ORDER BY created_utc, entry_id;
            """;

        return LoadPayloadRowsAsync<LedgerEntry>(connection, transaction, sql, cancellationToken);
    }

    private Task<List<BankTransaction>> LoadTransactionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT payload::text
            FROM bank_transactions
            WHERE state_key = @stateKey
            ORDER BY created_utc, transaction_id;
            """;

        return LoadPayloadRowsAsync<BankTransaction>(connection, transaction, sql, cancellationToken);
    }

    private Task<List<BankTransfer>> LoadTransfersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT payload::text
            FROM bank_transfers
            WHERE state_key = @stateKey
            ORDER BY requested_utc, transfer_id;
            """;

        return LoadPayloadRowsAsync<BankTransfer>(connection, transaction, sql, cancellationToken);
    }

    private Task<List<DisputeTicket>> LoadDisputesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT payload::text
            FROM bank_disputes
            WHERE state_key = @stateKey
            ORDER BY created_utc, dispute_id;
            """;

        return LoadPayloadRowsAsync<DisputeTicket>(connection, transaction, sql, cancellationToken);
    }

    private async Task<SupportingBankState> LoadSupportingStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT payload::text
            FROM bank_supporting_state
            WHERE state_key = @stateKey;
            """;

        await using NpgsqlCommand command = new(sql, connection, transaction);
        command.Parameters.AddWithValue("stateKey", _stateKey);

        object? payload = await command.ExecuteScalarAsync(cancellationToken);
        string? json = payload as string ?? Convert.ToString(payload);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SupportingBankState();
        }

        SupportingBankState? state = JsonSerializer.Deserialize<SupportingBankState>(json, _jsonOptions);
        return state ?? new SupportingBankState();
    }

    private async Task SaveSupportingStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        BankDatabase database,
        CancellationToken cancellationToken)
    {
        SupportingBankState supporting = new()
        {
            LinkedExternalAccounts = database.LinkedExternalAccounts,
            Budgets = database.Budgets,
            Goals = database.Goals,
            Notifications = database.Notifications,
            LoginHistory = database.LoginHistory,
            Devices = database.Devices,
            AuditLogs = database.AuditLogs,
            Statements = database.Statements,
            IdempotencyKeys = database.IdempotencyKeys,
            DomainEvents = database.DomainEvents
        };

        string payload = JsonSerializer.Serialize(supporting, _jsonOptions);

        const string sql = """
            INSERT INTO bank_supporting_state (state_key, payload, updated_utc)
            VALUES (@stateKey, CAST(@payload AS jsonb), NOW())
            ON CONFLICT (state_key) DO UPDATE SET
                payload = EXCLUDED.payload,
                updated_utc = EXCLUDED.updated_utc;
            """;

        await using NpgsqlCommand command = new(sql, connection, transaction);
        command.Parameters.AddWithValue("stateKey", _stateKey);
        command.Parameters.AddWithValue("payload", payload);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ReplaceUsersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<BankUser> users,
        CancellationToken cancellationToken)
    {
        await DeleteRowsAsync(connection, transaction, "bank_users", cancellationToken);

        const string sql = """
            INSERT INTO bank_users (
                state_key,
                user_id,
                email,
                role,
                created_utc,
                payload)
            VALUES (
                @stateKey,
                @userId,
                @email,
                @role,
                @createdUtc,
                CAST(@payload AS jsonb));
            """;

        foreach (BankUser user in users)
        {
            await using NpgsqlCommand command = new(sql, connection, transaction);
            command.Parameters.AddWithValue("stateKey", _stateKey);
            command.Parameters.AddWithValue("userId", user.Id);
            command.Parameters.AddWithValue("email", user.Email ?? string.Empty);
            command.Parameters.AddWithValue("role", (int)user.Role);
            command.Parameters.AddWithValue("createdUtc", user.CreatedUtc);
            command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(user, _jsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task ReplaceAccountsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<BankAccount> accounts,
        CancellationToken cancellationToken)
    {
        await DeleteRowsAsync(connection, transaction, "bank_accounts", cancellationToken);

        const string sql = """
            INSERT INTO bank_accounts (
                state_key,
                account_id,
                user_id,
                account_type,
                status,
                is_system_account,
                created_utc,
                payload)
            VALUES (
                @stateKey,
                @accountId,
                @userId,
                @accountType,
                @status,
                @isSystemAccount,
                @createdUtc,
                CAST(@payload AS jsonb));
            """;

        foreach (BankAccount account in accounts)
        {
            await using NpgsqlCommand command = new(sql, connection, transaction);
            command.Parameters.AddWithValue("stateKey", _stateKey);
            command.Parameters.AddWithValue("accountId", account.Id);
            command.Parameters.AddWithValue("userId", account.UserId);
            command.Parameters.AddWithValue("accountType", (int)account.Type);
            command.Parameters.AddWithValue("status", (int)account.Status);
            command.Parameters.AddWithValue("isSystemAccount", account.IsSystemAccount);
            command.Parameters.AddWithValue("createdUtc", account.CreatedUtc);
            command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(account, _jsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task ReplaceLedgerEntriesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<LedgerEntry> ledgerEntries,
        CancellationToken cancellationToken)
    {
        await DeleteRowsAsync(connection, transaction, "bank_ledger_entries", cancellationToken);

        const string sql = """
            INSERT INTO bank_ledger_entries (
                state_key,
                entry_id,
                account_id,
                user_id,
                transfer_id,
                created_utc,
                entry_type,
                payload)
            VALUES (
                @stateKey,
                @entryId,
                @accountId,
                @userId,
                @transferId,
                @createdUtc,
                @entryType,
                CAST(@payload AS jsonb));
            """;

        foreach (LedgerEntry entry in ledgerEntries)
        {
            await using NpgsqlCommand command = new(sql, connection, transaction);
            command.Parameters.AddWithValue("stateKey", _stateKey);
            command.Parameters.AddWithValue("entryId", entry.Id);
            command.Parameters.AddWithValue("accountId", entry.AccountId);
            command.Parameters.AddWithValue("userId", entry.UserId);
            command.Parameters.AddWithValue("transferId", (object?)entry.TransferId ?? DBNull.Value);
            command.Parameters.AddWithValue("createdUtc", entry.CreatedUtc);
            command.Parameters.AddWithValue("entryType", entry.EntryType ?? string.Empty);
            command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(entry, _jsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task ReplaceTransactionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<BankTransaction> transactions,
        CancellationToken cancellationToken)
    {
        await DeleteRowsAsync(connection, transaction, "bank_transactions", cancellationToken);

        const string sql = """
            INSERT INTO bank_transactions (
                state_key,
                transaction_id,
                user_id,
                account_id,
                transfer_id,
                created_utc,
                posted_utc,
                state,
                amount,
                category,
                payload)
            VALUES (
                @stateKey,
                @transactionId,
                @userId,
                @accountId,
                @transferId,
                @createdUtc,
                @postedUtc,
                @state,
                @amount,
                @category,
                CAST(@payload AS jsonb));
            """;

        foreach (BankTransaction transactionRow in transactions)
        {
            await using NpgsqlCommand command = new(sql, connection, transaction);
            command.Parameters.AddWithValue("stateKey", _stateKey);
            command.Parameters.AddWithValue("transactionId", transactionRow.Id);
            command.Parameters.AddWithValue("userId", transactionRow.UserId);
            command.Parameters.AddWithValue("accountId", transactionRow.AccountId);
            command.Parameters.AddWithValue("transferId", (object?)transactionRow.TransferId ?? DBNull.Value);
            command.Parameters.AddWithValue("createdUtc", transactionRow.CreatedUtc);
            command.Parameters.AddWithValue("postedUtc", (object?)transactionRow.PostedUtc ?? DBNull.Value);
            command.Parameters.AddWithValue("state", (int)transactionRow.State);
            command.Parameters.AddWithValue("amount", decimal.Round(transactionRow.Amount, 2, MidpointRounding.AwayFromZero));
            command.Parameters.AddWithValue("category", transactionRow.Category ?? string.Empty);
            command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(transactionRow, _jsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task ReplaceTransfersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<BankTransfer> transfers,
        CancellationToken cancellationToken)
    {
        await DeleteRowsAsync(connection, transaction, "bank_transfers", cancellationToken);

        const string sql = """
            INSERT INTO bank_transfers (
                state_key,
                transfer_id,
                user_id,
                source_account_id,
                destination_internal_account_id,
                destination_external_account_id,
                state,
                frequency,
                requested_utc,
                settles_utc,
                fraud_score,
                payload)
            VALUES (
                @stateKey,
                @transferId,
                @userId,
                @sourceAccountId,
                @destinationInternalAccountId,
                @destinationExternalAccountId,
                @state,
                @frequency,
                @requestedUtc,
                @settlesUtc,
                @fraudScore,
                CAST(@payload AS jsonb));
            """;

        foreach (BankTransfer transfer in transfers)
        {
            await using NpgsqlCommand command = new(sql, connection, transaction);
            command.Parameters.AddWithValue("stateKey", _stateKey);
            command.Parameters.AddWithValue("transferId", transfer.Id);
            command.Parameters.AddWithValue("userId", transfer.UserId);
            command.Parameters.AddWithValue("sourceAccountId", transfer.SourceAccountId);
            command.Parameters.AddWithValue("destinationInternalAccountId", (object?)transfer.DestinationInternalAccountId ?? DBNull.Value);
            command.Parameters.AddWithValue("destinationExternalAccountId", (object?)transfer.DestinationExternalAccountId ?? DBNull.Value);
            command.Parameters.AddWithValue("state", (int)transfer.State);
            command.Parameters.AddWithValue("frequency", (int)transfer.Frequency);
            command.Parameters.AddWithValue("requestedUtc", transfer.RequestedUtc);
            command.Parameters.AddWithValue("settlesUtc", (object?)transfer.SettlesUtc ?? DBNull.Value);
            command.Parameters.AddWithValue("fraudScore", transfer.FraudScore);
            command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(transfer, _jsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task ReplaceDisputesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<DisputeTicket> disputes,
        CancellationToken cancellationToken)
    {
        await DeleteRowsAsync(connection, transaction, "bank_disputes", cancellationToken);

        const string sql = """
            INSERT INTO bank_disputes (
                state_key,
                dispute_id,
                user_id,
                transaction_id,
                status,
                created_utc,
                payload)
            VALUES (
                @stateKey,
                @disputeId,
                @userId,
                @transactionId,
                @status,
                @createdUtc,
                CAST(@payload AS jsonb));
            """;

        foreach (DisputeTicket dispute in disputes)
        {
            await using NpgsqlCommand command = new(sql, connection, transaction);
            command.Parameters.AddWithValue("stateKey", _stateKey);
            command.Parameters.AddWithValue("disputeId", dispute.Id);
            command.Parameters.AddWithValue("userId", dispute.UserId);
            command.Parameters.AddWithValue("transactionId", dispute.TransactionId);
            command.Parameters.AddWithValue("status", (int)dispute.Status);
            command.Parameters.AddWithValue("createdUtc", dispute.CreatedUtc);
            command.Parameters.AddWithValue("payload", JsonSerializer.Serialize(dispute, _jsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task DeleteRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        string sql = $"DELETE FROM {tableName} WHERE state_key = @stateKey;";

        await using NpgsqlCommand command = new(sql, connection, transaction);
        command.Parameters.AddWithValue("stateKey", _stateKey);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<List<T>> LoadPayloadRowsAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        List<T> items = new();

        await using NpgsqlCommand command = new(sql, connection, transaction);
        command.Parameters.AddWithValue("stateKey", _stateKey);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0))
            {
                continue;
            }

            string json = reader.GetString(0);
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            T? item = JsonSerializer.Deserialize<T>(json, _jsonOptions);
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items;
    }

    private async Task<BankDatabase?> TryLoadLegacyStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string existsSql = """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.tables
                WHERE table_schema = current_schema()
                  AND table_name = 'bank_state');
            """;

        await using (NpgsqlCommand existsCommand = new(existsSql, connection, transaction))
        {
            object? existsResult = await existsCommand.ExecuteScalarAsync(cancellationToken);
            bool tableExists = existsResult is bool value && value;
            if (!tableExists)
            {
                return null;
            }
        }

        const string loadSql = """
            SELECT payload::text
            FROM bank_state
            WHERE state_key = @stateKey;
            """;

        await using NpgsqlCommand command = new(loadSql, connection, transaction);
        command.Parameters.AddWithValue("stateKey", _stateKey);

        object? payload = await command.ExecuteScalarAsync(cancellationToken);
        string? json = payload as string ?? Convert.ToString(payload);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        BankDatabase? legacyDatabase = JsonSerializer.Deserialize<BankDatabase>(json, _jsonOptions);
        return legacyDatabase;
    }

    private static bool HasPersistedRows(BankDatabase database)
    {
        return database.Users.Count > 0 ||
               database.Accounts.Count > 0 ||
               database.LinkedExternalAccounts.Count > 0 ||
               database.Transactions.Count > 0 ||
               database.LedgerEntries.Count > 0 ||
               database.Transfers.Count > 0 ||
               database.Budgets.Count > 0 ||
               database.Goals.Count > 0 ||
               database.Notifications.Count > 0 ||
               database.LoginHistory.Count > 0 ||
               database.Devices.Count > 0 ||
               database.Disputes.Count > 0 ||
               database.AuditLogs.Count > 0 ||
               database.Statements.Count > 0 ||
               database.IdempotencyKeys.Count > 0 ||
               database.DomainEvents.Count > 0;
    }

    private static string Require(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"PostgreSQL setting '{label}' is required.");
        }

        return value.Trim();
    }

    private sealed class SupportingBankState
    {
        public List<LinkedExternalAccount> LinkedExternalAccounts { get; set; } = new();
        public List<BudgetRule> Budgets { get; set; } = new();
        public List<SavingsGoal> Goals { get; set; } = new();
        public List<NotificationItem> Notifications { get; set; } = new();
        public List<LoginRecord> LoginHistory { get; set; } = new();
        public List<DeviceRecord> Devices { get; set; } = new();
        public List<AuditLogEntry> AuditLogs { get; set; } = new();
        public List<StatementRecord> Statements { get; set; } = new();
        public List<IdempotencyRecord> IdempotencyKeys { get; set; } = new();
        public List<DomainEventRecord> DomainEvents { get; set; } = new();
    }
}

public sealed record PostgresBankingStoreOptions(
    string ConnectionString,
    string StateKey = "bank_state");
