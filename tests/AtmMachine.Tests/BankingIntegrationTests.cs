using AtmMachine.WebUI.Banking.Infrastructure;
using AtmMachine.WebUI.Banking.Models;
using AtmMachine.WebUI.Banking.Services;
using AtmMachine.WebUI.Observability;
using AtmMachine.WebUI.Realtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace AtmMachine.Tests;

public sealed class BankingIntegrationTests
{
    [Fact]
    public async Task Login_EndToEnd_SucceedsAndRecordsDevice()
    {
        await using BankingHarness harness = await BankingHarness.CreateAsync();

        LoginCompleteResult complete = await harness.Service.LoginAsync(
            "spencer@example.com",
            "Password123!",
            "device-e2e-1",
            "IntegrationSuite",
            "127.0.0.1",
            "IntegrationTest/1.0");

        Assert.True(complete.IsSuccess);
        Assert.NotNull(complete.UserId);
        Assert.False(string.IsNullOrWhiteSpace(complete.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(complete.RefreshToken));

        IReadOnlyList<LoginRecord> history = await harness.Service.GetLoginHistoryAsync(complete.UserId!.Value);
        Assert.Contains(history, item => item.DeviceId == "device-e2e-1");
    }

    [Fact]
    public async Task ExternalTransferSettlement_EndToEnd_PendingBecomesPosted()
    {
        await using BankingHarness harness = await BankingHarness.CreateAsync();

        LoginCompleteResult user = await LoginAsUserAsync(harness.Service);
        IReadOnlyList<BankAccount> accounts = await harness.Service.GetAccountsAsync(user.UserId!.Value);
        BankAccount source = accounts.First(account => account.Type == BankAccountType.Checking);

        OperationResult linkResult = await harness.Service.LinkExternalAccountAsync(
            user.UserId.Value,
            "Acme Federal",
            "Payroll",
            "****7732");
        Assert.True(linkResult.IsSuccess);

        IReadOnlyList<LinkedExternalAccount> externalAccounts = await harness.Service.GetLinkedExternalAccountsAsync(user.UserId.Value);
        LinkedExternalAccount external = Assert.Single(externalAccounts);

        TransferActionResult transfer = await harness.Service.CreateTransferAsync(
            user.UserId.Value,
            new TransferRequest
            {
                SourceAccountId = source.Id,
                DestinationExternalAccountId = external.Id,
                Amount = 75.00m,
                Memo = "E2E settlement test",
                Frequency = TransferFrequency.OneTime,
                IdempotencyKey = $"e2e-{Guid.NewGuid():N}"
            },
            user.Role);

        Assert.True(transfer.IsSuccess);
        Assert.Equal(TransferState.Pending, transfer.State);
        Assert.NotNull(transfer.TransferId);

        await harness.Store.WriteAsync(database =>
        {
            BankTransfer pendingTransfer = database.Transfers.First(item => item.Id == transfer.TransferId!.Value);
            pendingTransfer.SettlesUtc = DateTimeOffset.UtcNow.AddSeconds(-2);
        });

        int processed = await harness.Service.RunSettlementAsync();
        Assert.True(processed >= 1);

        TransferState transferState = await harness.Store.ReadAsync(database =>
            database.Transfers.First(item => item.Id == transfer.TransferId!.Value).State);
        Assert.Equal(TransferState.Posted, transferState);

        LedgerPageResult? ledger = await harness.Service.GetLedgerAsync(
            user.UserId.Value,
            new LedgerFilter(source.Id, null, null, null, null, null));

        Assert.NotNull(ledger);
        Assert.Contains(ledger!.Transactions, tx =>
            tx.MerchantName == "ACH External" &&
            tx.Amount == -75.00m &&
            tx.State == TransactionState.Posted);
    }

    [Fact]
    public async Task InternalTransfer_WritesBalancedLedgerEntries_WithSharedCorrelationMetadata()
    {
        await using BankingHarness harness = await BankingHarness.CreateAsync();

        LoginCompleteResult user = await LoginAsUserAsync(harness.Service);
        IReadOnlyList<BankAccount> accounts = await harness.Service.GetAccountsAsync(user.UserId!.Value);
        BankAccount source = accounts.First(account => account.Type == BankAccountType.Checking);
        BankAccount destination = accounts.First(account => account.Type == BankAccountType.Savings);

        TransferRequest request = new()
        {
            SourceAccountId = source.Id,
            DestinationInternalAccountId = destination.Id,
            Amount = 25.00m,
            Memo = "Ledger invariant test",
            Frequency = TransferFrequency.OneTime,
            IdempotencyKey = $"ledger-{Guid.NewGuid():N}"
        };

        TransferActionResult transfer = await harness.Service.CreateTransferAsync(
            user.UserId.Value,
            request,
            user.Role);

        Assert.True(transfer.IsSuccess);
        Assert.Equal(TransferState.Posted, transfer.State);
        Assert.NotNull(transfer.TransferId);

        (List<LedgerEntry> Entries, List<BankTransaction> Transactions) ledgerState = await harness.Store.ReadAsync(database =>
        {
            List<LedgerEntry> entries = database.LedgerEntries
                .Where(entry => entry.TransferId == transfer.TransferId!.Value)
                .ToList();
            List<BankTransaction> transactions = database.Transactions
                .Where(item => item.TransferId == transfer.TransferId!.Value)
                .ToList();
            return (entries, transactions);
        });

        Assert.Equal(2, ledgerState.Entries.Count);
        Assert.Equal(2, ledgerState.Transactions.Count);

        LedgerEntry debit = Assert.Single(ledgerState.Entries.Where(entry => entry.AvailableDelta < 0m));
        LedgerEntry credit = Assert.Single(ledgerState.Entries.Where(entry => entry.AvailableDelta > 0m));

        Assert.Equal(-25.00m, debit.AvailableDelta);
        Assert.Equal(-25.00m, debit.LedgerDelta);
        Assert.Equal(25.00m, credit.AvailableDelta);
        Assert.Equal(25.00m, credit.LedgerDelta);
        Assert.Equal(debit.CorrelationId, credit.CorrelationId);
        Assert.Equal(request.IdempotencyKey, debit.IdempotencyKey);
        Assert.Equal(request.IdempotencyKey, credit.IdempotencyKey);
        Assert.All(ledgerState.Transactions, item =>
        {
            Assert.Equal(debit.CorrelationId, item.CorrelationId);
            Assert.Equal(request.IdempotencyKey, item.IdempotencyKey);
        });
    }

    [Fact]
    public async Task PeerTransferByEmail_EndToEnd_PostsFundsToRecipient()
    {
        await using BankingHarness harness = await BankingHarness.CreateAsync();

        OperationResult signup = await harness.Service.SignupAsync(new SignupRequest(
            "alex@example.com",
            "Password123!",
            "Alex",
            "Rivera",
            "22 Oak Street",
            "555-0222"));
        Assert.True(signup.IsSuccess);

        LoginCompleteResult sender = await LoginAsUserAsync(harness.Service);
        IReadOnlyList<BankAccount> senderAccountsBefore = await harness.Service.GetAccountsAsync(sender.UserId!.Value);
        BankAccount senderSourceBefore = senderAccountsBefore.First(account => account.Type == BankAccountType.Checking);

        Guid recipientUserId = await harness.Store.ReadAsync(database =>
            database.Users.First(user => user.Email == "alex@example.com").Id);
        IReadOnlyList<BankAccount> recipientAccountsBefore = await harness.Service.GetAccountsAsync(recipientUserId);
        BankAccount recipientCheckingBefore = recipientAccountsBefore.First(account => account.Type == BankAccountType.Checking);

        decimal transferAmount = 42.25m;
        TransferActionResult transfer = await harness.Service.CreatePeerTransferByEmailAsync(
            sender.UserId.Value,
            new PeerTransferByEmailRequest
            {
                SourceAccountId = senderSourceBefore.Id,
                RecipientEmail = "alex@example.com",
                PreferredDestinationAccountType = BankAccountType.Checking,
                Amount = transferAmount,
                Memo = "Dinner split",
                Frequency = TransferFrequency.OneTime,
                IdempotencyKey = $"peer-email-{Guid.NewGuid():N}"
            },
            sender.Role);

        Assert.True(transfer.IsSuccess);
        Assert.Equal(TransferState.Posted, transfer.State);
        Assert.NotNull(transfer.TransferId);

        IReadOnlyList<BankAccount> senderAccountsAfter = await harness.Service.GetAccountsAsync(sender.UserId.Value);
        IReadOnlyList<BankAccount> recipientAccountsAfter = await harness.Service.GetAccountsAsync(recipientUserId);
        BankAccount senderSourceAfter = senderAccountsAfter.First(account => account.Id == senderSourceBefore.Id);
        BankAccount recipientCheckingAfter = recipientAccountsAfter.First(account => account.Id == recipientCheckingBefore.Id);

        Assert.Equal(senderSourceBefore.AvailableBalance - transferAmount, senderSourceAfter.AvailableBalance);
        Assert.Equal(recipientCheckingBefore.AvailableBalance + transferAmount, recipientCheckingAfter.AvailableBalance);

        (List<LedgerEntry> Entries, List<BankTransaction> Transactions) transferState =
            await harness.Store.ReadAsync(database =>
            {
                List<LedgerEntry> entries = database.LedgerEntries
                    .Where(entry => entry.TransferId == transfer.TransferId!.Value)
                    .ToList();
                List<BankTransaction> transactions = database.Transactions
                    .Where(item => item.TransferId == transfer.TransferId!.Value)
                    .ToList();
                return (entries, transactions);
            });

        Assert.Equal(2, transferState.Entries.Count);
        Assert.Equal(2, transferState.Transactions.Count);
        Assert.Contains(transferState.Transactions, transaction =>
            transaction.UserId == sender.UserId.Value &&
            transaction.Amount == -transferAmount &&
            transaction.MerchantName == "P2P Transfer");
        Assert.Contains(transferState.Transactions, transaction =>
            transaction.UserId == recipientUserId &&
            transaction.Amount == transferAmount &&
            transaction.MerchantName == "P2P Transfer");
    }

    [Fact]
    public async Task OpenAccount_WithOpeningDeposit_WritesBalancedLedgerAndCreatesAccount()
    {
        await using BankingHarness harness = await BankingHarness.CreateAsync();

        LoginCompleteResult user = await LoginAsUserAsync(harness.Service);
        IReadOnlyList<BankAccount> before = await harness.Service.GetAccountsAsync(user.UserId!.Value);
        BankAccount source = before.First(account => account.Type == BankAccountType.Checking);
        decimal sourceBefore = source.AvailableBalance;

        AccountOpenResult opened = await harness.Service.OpenAccountAsync(
            user.UserId.Value,
            BankAccountType.Checking,
            "Travel Card",
            source.Id,
            125.00m);

        Assert.True(opened.IsSuccess);
        Assert.NotNull(opened.AccountId);

        string idempotencyKey = $"account-open-{opened.AccountId!.Value:N}";

        IReadOnlyList<BankAccount> after = await harness.Service.GetAccountsAsync(user.UserId.Value);
        Assert.Equal(before.Count + 1, after.Count);

        BankAccount newAccount = Assert.Single(after.Where(account => account.Id == opened.AccountId.Value));
        Assert.Equal(BankAccountType.Checking, newAccount.Type);
        Assert.Equal("Travel Card", newAccount.Nickname);
        Assert.Equal(125.00m, newAccount.AvailableBalance);

        BankAccount sourceAfter = Assert.Single(after.Where(account => account.Id == source.Id));
        Assert.Equal(sourceBefore - 125.00m, sourceAfter.AvailableBalance);

        (List<LedgerEntry> Entries, List<BankTransaction> Transactions, List<DomainEventRecord> DomainEvents) state =
            await harness.Store.ReadAsync(database =>
            {
                List<LedgerEntry> entries = database.LedgerEntries
                    .Where(entry => string.Equals(entry.IdempotencyKey, idempotencyKey, StringComparison.Ordinal))
                    .ToList();
                List<BankTransaction> transactions = database.Transactions
                    .Where(item => string.Equals(item.IdempotencyKey, idempotencyKey, StringComparison.Ordinal))
                    .ToList();
                List<DomainEventRecord> domainEvents = database.DomainEvents
                    .Where(item => item.EventType == "AccountOpened" && item.EntityId == opened.AccountId.Value.ToString("N"))
                    .ToList();
                return (entries, transactions, domainEvents);
            });

        Assert.Equal(2, state.Entries.Count);
        Assert.Equal(2, state.Transactions.Count);
        Assert.Contains(state.Entries, entry => entry.AccountId == source.Id && entry.AvailableDelta == -125.00m);
        Assert.Contains(state.Entries, entry => entry.AccountId == opened.AccountId.Value && entry.AvailableDelta == 125.00m);
        Assert.Single(state.DomainEvents);
    }

    [Fact]
    public async Task SuspiciousTransfer_IsHeldUnderReview_AndPublishesFraudSignals()
    {
        await using BankingHarness harness = await BankingHarness.CreateAsync();

        LoginCompleteResult user = await harness.Service.LoginAsync(
            "spencer@example.com",
            "Password123!",
            "device-fraud-review",
            "IntegrationFraudDevice",
            "203.0.113.10",
            "IntegrationTest/Fraud");
        Assert.True(user.IsSuccess);

        harness.RealtimeQueue.Drain(64);

        IReadOnlyList<BankAccount> accounts = await harness.Service.GetAccountsAsync(user.UserId!.Value);
        BankAccount source = accounts.First(account => account.Type == BankAccountType.Checking);
        BankAccount destination = accounts.First(account => account.Type == BankAccountType.Savings);

        TransferActionResult transfer = await harness.Service.CreateTransferAsync(
            user.UserId.Value,
            new TransferRequest
            {
                SourceAccountId = source.Id,
                DestinationInternalAccountId = destination.Id,
                Amount = 1500.00m,
                Memo = "Fraud review test",
                Frequency = TransferFrequency.OneTime,
                IdempotencyKey = $"fraud-{Guid.NewGuid():N}"
            },
            user.Role);

        Assert.True(transfer.IsSuccess);
        Assert.Equal(TransferState.UnderReview, transfer.State);
        Assert.NotNull(transfer.TransferId);

        (BankTransfer Transfer, List<DomainEventRecord> DomainEvents) stored = await harness.Store.ReadAsync(database =>
        {
            BankTransfer savedTransfer = database.Transfers.First(item => item.Id == transfer.TransferId!.Value);
            List<DomainEventRecord> domainEvents = database.DomainEvents
                .Where(item => item.EntityType == "transfer" && item.EntityId == transfer.TransferId.Value.ToString("N"))
                .ToList();
            return (savedTransfer, domainEvents);
        });

        Assert.True(stored.Transfer.FraudScore >= 50);
        Assert.Contains("amount:", stored.Transfer.FraudFlags, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("new-device", stored.Transfer.FraudFlags, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Queued for fraud review.", stored.Transfer.ReviewNotes);
        Assert.Contains(stored.DomainEvents, item => item.EventType == "TransferCreated");
        Assert.Contains(stored.DomainEvents, item => item.EventType == "TransferFlaggedForReview");

        AdminSnapshot snapshot = await harness.Service.GetAdminSnapshotAsync();
        Assert.Contains(snapshot.FraudTransfers, item =>
            item.TransferId == transfer.TransferId.Value &&
            item.State == TransferState.UnderReview);

        List<RealtimeEnvelope> realtimeEvents = harness.RealtimeQueue.Drain(64);
        Assert.Contains(realtimeEvents, item => item.EventName == "transfer.status");
        Assert.Contains(realtimeEvents, item => item.EventName == "fraud.alert");
        Assert.Contains(realtimeEvents, item => item.EventName == "audit.feed");
    }

    [Fact]
    public async Task AdminApproval_PostsReviewedTransfer_AndPublishesDomainAndRealtimeUpdates()
    {
        await using BankingHarness harness = await BankingHarness.CreateAsync();

        LoginCompleteResult user = await harness.Service.LoginAsync(
            "spencer@example.com",
            "Password123!",
            "device-fraud-approve",
            "IntegrationFraudApproveDevice",
            "203.0.113.15",
            "IntegrationTest/FraudApprove");
        Assert.True(user.IsSuccess);

        IReadOnlyList<BankAccount> accounts = await harness.Service.GetAccountsAsync(user.UserId!.Value);
        BankAccount source = accounts.First(account => account.Type == BankAccountType.Checking);
        BankAccount destination = accounts.First(account => account.Type == BankAccountType.Savings);

        TransferActionResult created = await harness.Service.CreateTransferAsync(
            user.UserId.Value,
            new TransferRequest
            {
                SourceAccountId = source.Id,
                DestinationInternalAccountId = destination.Id,
                Amount = 1600.00m,
                Memo = "Manual approval path",
                Frequency = TransferFrequency.OneTime,
                IdempotencyKey = $"review-{Guid.NewGuid():N}"
            },
            user.Role);

        Assert.True(created.IsSuccess);
        Assert.Equal(TransferState.UnderReview, created.State);
        Assert.NotNull(created.TransferId);

        LoginCompleteResult admin = await LoginAsAdminAsync(harness.Service);
        harness.RealtimeQueue.Drain(128);

        OperationResult approval = await harness.Service.AdminReviewTransferAsync(
            admin.UserId!.Value,
            created.TransferId.Value,
            approve: true,
            adminNotes: "Approved after manual verification.");

        Assert.True(approval.IsSuccess);

        (BankTransfer Transfer, List<LedgerEntry> Entries, List<BankTransaction> Transactions, List<DomainEventRecord> DomainEvents) state =
            await harness.Store.ReadAsync(database =>
            {
                BankTransfer transfer = database.Transfers.First(item => item.Id == created.TransferId.Value);
                List<LedgerEntry> entries = database.LedgerEntries
                    .Where(item => item.TransferId == created.TransferId.Value)
                    .ToList();
                List<BankTransaction> transactions = database.Transactions
                    .Where(item => item.TransferId == created.TransferId.Value)
                    .ToList();
                List<DomainEventRecord> domainEvents = database.DomainEvents
                    .Where(item => item.EntityType == "transfer" && item.EntityId == created.TransferId.Value.ToString("N"))
                    .ToList();
                return (transfer, entries, transactions, domainEvents);
            });

        Assert.Equal(TransferState.Posted, state.Transfer.State);
        Assert.Equal(admin.UserId.Value, state.Transfer.ReviewedByUserId);
        Assert.False(string.IsNullOrWhiteSpace(state.Transfer.ReviewNotes));
        Assert.NotNull(state.Transfer.ReviewedUtc);
        Assert.Equal(2, state.Entries.Count);
        Assert.Equal(2, state.Transactions.Count);
        Assert.Contains(state.DomainEvents, item => item.EventType == "TransferApproved");

        List<RealtimeEnvelope> realtimeEvents = harness.RealtimeQueue.Drain(128);
        Assert.Contains(realtimeEvents, item => item.EventName == "transfer.status");
        Assert.True(realtimeEvents.Count(item => item.EventName == "account.balance") >= 2);
        Assert.Contains(realtimeEvents, item => item.EventName == "audit.feed");
    }

    [Fact]
    public async Task DisputeLifecycle_EndToEnd_UserCreatesAndAdminResolves()
    {
        await using BankingHarness harness = await BankingHarness.CreateAsync();

        LoginCompleteResult user = await LoginAsUserAsync(harness.Service);
        LedgerPageResult? ledger = await harness.Service.GetLedgerAsync(
            user.UserId!.Value,
            new LedgerFilter(null, null, null, null, null, null));
        Assert.NotNull(ledger);

        BankTransaction? transaction = ledger!.Transactions.FirstOrDefault();
        Assert.NotNull(transaction);

        OperationResult create = await harness.Service.CreateDisputeAsync(
            user.UserId.Value,
            transaction!.Id,
            "Unauthorized charge",
            "I do not recognize this merchant.");
        Assert.True(create.IsSuccess);

        IReadOnlyList<DisputeTicket> userDisputes = await harness.Service.GetDisputesAsync(user.UserId.Value);
        DisputeTicket created = Assert.Single(userDisputes);
        Assert.Equal(DisputeStatus.Open, created.Status);

        LoginCompleteResult admin = await LoginAsAdminAsync(harness.Service);
        OperationResult review = await harness.Service.AdminReviewDisputeAsync(
            admin.UserId!.Value,
            created.Id,
            DisputeStatus.Resolved,
            "Refund issued.");
        Assert.True(review.IsSuccess);

        IReadOnlyList<DisputeTicket> updated = await harness.Service.GetDisputesAsync(user.UserId.Value);
        DisputeTicket resolved = Assert.Single(updated);
        Assert.Equal(DisputeStatus.Resolved, resolved.Status);
        Assert.Equal("Refund issued.", resolved.AdminNotes);
    }

    private static async Task<LoginCompleteResult> LoginAsUserAsync(BankingService service)
    {
        LoginCompleteResult complete = await service.LoginAsync(
            "spencer@example.com",
            "Password123!",
            "device-user",
            "IntegrationUserDevice",
            "127.0.0.1",
            "IntegrationTest/User");

        Assert.True(complete.IsSuccess);
        return complete;
    }

    private static async Task<LoginCompleteResult> LoginAsAdminAsync(BankingService service)
    {
        LoginCompleteResult complete = await service.LoginAsync(
            "admin@consoleatm.local",
            "Admin123!",
            "device-admin",
            "IntegrationAdminDevice",
            "127.0.0.1",
            "IntegrationTest/Admin");

        Assert.True(complete.IsSuccess);
        Assert.Equal(UserRole.Admin, complete.Role);
        return complete;
    }

    private sealed class BankingHarness : IAsyncDisposable
    {
        private readonly string _tempDirectory;
        private readonly BankingTelemetry _telemetry;

        private BankingHarness(
            string tempDirectory,
            IBankingDataStore store,
            BankingService service,
            BankingTelemetry telemetry,
            BankingRealtimeQueue realtimeQueue)
        {
            _tempDirectory = tempDirectory;
            Store = store;
            Service = service;
            _telemetry = telemetry;
            RealtimeQueue = realtimeQueue;
        }

        public IBankingDataStore Store { get; }
        public BankingService Service { get; }
        public BankingRealtimeQueue RealtimeQueue { get; }

        public static async Task<BankingHarness> CreateAsync()
        {
            string tempDirectory = Path.Combine(Path.GetTempPath(), $"consoleatm-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirectory);
            string dbPath = Path.Combine(tempDirectory, "banking-integration.sqlite");

            IBankingDataStore store = new SqliteBankingDataStore(dbPath);
            BankingSecurityService security = new();
            FirebaseAuthClient firebaseAuthClient = new(
                new HttpClient(),
                new FirebaseAuthOptions(
                    Enabled: false,
                    ApiKey: string.Empty,
                    Endpoint: "https://identitytoolkit.googleapis.com/v1"));
            BankingTelemetry telemetry = new();
            BankingRealtimeQueue realtimeQueue = new();
            BankingService service = new(
                store,
                security,
                firebaseAuthClient,
                telemetry,
                realtimeQueue,
                NullLogger<BankingService>.Instance);
            await service.SeedAsync();

            return new BankingHarness(tempDirectory, store, service, telemetry, realtimeQueue);
        }

        public ValueTask DisposeAsync()
        {
            _telemetry.Dispose();

            if (Directory.Exists(_tempDirectory))
            {
                try
                {
                    Directory.Delete(_tempDirectory, recursive: true);
                }
                catch
                {
                    // Best-effort cleanup for test temp files.
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}
