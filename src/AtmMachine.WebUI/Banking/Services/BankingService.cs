using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AtmMachine.WebUI.Banking.Infrastructure;
using AtmMachine.WebUI.Banking.Models;
using AtmMachine.WebUI.Observability;
using AtmMachine.WebUI.Realtime;

namespace AtmMachine.WebUI.Banking.Services;

public sealed class BankingService
{
    private static readonly Guid SystemUserId = Guid.Empty;
    private const string SystemSeedAccountKey = "system_seed_capital";
    private const string SystemOperatingAccountKey = "system_bank_ops";
    private const string SystemSettlementAccountKey = "system_ach_settlement";

    private readonly IBankingDataStore _dataStore;
    private readonly BankingSecurityService _securityService;
    private readonly FirebaseAuthClient _firebaseAuthClient;
    private readonly BankingTelemetry _telemetry;
    private readonly BankingRealtimeQueue _realtimeQueue;
    private readonly ILogger<BankingService> _logger;

    public BankingService(
        IBankingDataStore dataStore,
        BankingSecurityService securityService,
        FirebaseAuthClient firebaseAuthClient,
        BankingTelemetry telemetry,
        BankingRealtimeQueue realtimeQueue,
        ILogger<BankingService> logger)
    {
        _dataStore = dataStore;
        _securityService = securityService;
        _firebaseAuthClient = firebaseAuthClient;
        _telemetry = telemetry;
        _realtimeQueue = realtimeQueue;
        _logger = logger;
    }

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        await _dataStore.WriteAsync(database =>
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (database.Users.Count > 0)
            {
                CleanupExpiredRows(database);
                if (!database.Users.Any(existingUser => existingUser.Role == UserRole.Admin))
                {
                    var (existingAdminHash, existingAdminSalt) = _securityService.HashPassword("Admin123!");
                    BankUser adminOnly = new()
                    {
                        Email = "admin@consoleatm.local",
                        PasswordHash = existingAdminHash,
                        PasswordSalt = existingAdminSalt,
                        FirstName = "System",
                        LastName = "Admin",
                        Address = "1 Console Way",
                        Phone = "555-0100",
                        Role = UserRole.Admin
                    };

                    database.Users.Add(adminOnly);
                    database.AuditLogs.Add(new AuditLogEntry
                    {
                        ActorUserId = adminOnly.Id,
                        ActorRole = UserRole.Admin,
                        Action = "seed_admin_on_existing_data",
                        EntityType = "system",
                        EntityId = "bootstrap",
                        Metadata = $"Admin added for existing dataset at {now:O}."
                    });
                }

                return;
            }

            var (adminHash, adminSalt) = _securityService.HashPassword("Admin123!");
            BankUser admin = new()
            {
                Email = "admin@consoleatm.local",
                PasswordHash = adminHash,
                PasswordSalt = adminSalt,
                FirstName = "System",
                LastName = "Admin",
                Address = "1 Console Way",
                Phone = "555-0100",
                Role = UserRole.Admin
            };

            var (userHash, userSalt) = _securityService.HashPassword("Password123!");
            BankUser user = new()
            {
                Email = "spencer@example.com",
                PasswordHash = userHash,
                PasswordSalt = userSalt,
                FirstName = "Spencer",
                LastName = "Jones",
                Address = "100 Main Street",
                Phone = "555-0178",
                Role = UserRole.User
            };

            database.Users.Add(admin);
            database.Users.Add(user);

            BankAccount checking = CreateAccount(
                user.Id,
                BankAccountType.Checking,
                "Everyday Checking");
            BankAccount savings = CreateAccount(
                user.Id,
                BankAccountType.Savings,
                "Emergency Savings");

            database.Accounts.Add(checking);
            database.Accounts.Add(savings);

            string openingCorrelationId = GenerateCorrelationId();
            AppendOpeningBalanceEntries(
                database,
                checking,
                5147.73m,
                openingCorrelationId,
                "seed-checking-opening");
            AppendOpeningBalanceEntries(
                database,
                savings,
                1500.00m,
                openingCorrelationId,
                "seed-savings-opening");

            string payrollCorrelationId = GenerateCorrelationId();
            string payrollIdempotencyKey = "seed-payroll";
            AppendBalancedLedger(
                database,
                GetOrCreateSystemAccount(database, SystemOperatingAccountKey, "Bank Operations Clearing"),
                checking,
                3000.00m,
                3000.00m,
                "seed_payroll",
                payrollCorrelationId,
                payrollIdempotencyKey,
                RecordActorType.System,
                null,
                "Seed payroll funding");

            database.Transactions.Add(CreateTransaction(
                user.Id,
                checking.Id,
                3000.00m,
                "Income",
                "Employer Payroll",
                "Monthly paycheck",
                TransactionState.Posted,
                payrollCorrelationId,
                payrollIdempotencyKey,
                RecordActorType.System,
                null));

            string diningCorrelationId = GenerateCorrelationId();
            string diningIdempotencyKey = "seed-dining";
            AppendBalancedLedger(
                database,
                checking,
                GetOrCreateSystemAccount(database, SystemOperatingAccountKey, "Bank Operations Clearing"),
                122.50m,
                122.50m,
                "seed_spend",
                diningCorrelationId,
                diningIdempotencyKey,
                RecordActorType.System,
                null,
                "Seed dining spend");
            database.Transactions.Add(CreateTransaction(
                user.Id,
                checking.Id,
                -122.50m,
                "Dining",
                "Urban Table",
                "Dinner with friends",
                TransactionState.Posted,
                diningCorrelationId,
                diningIdempotencyKey,
                RecordActorType.System,
                null));

            string savingsCorrelationId = GenerateCorrelationId();
            string savingsIdempotencyKey = "seed-savings-transfer";
            AppendBalancedLedger(
                database,
                checking,
                savings,
                600.00m,
                600.00m,
                "seed_internal_transfer",
                savingsCorrelationId,
                savingsIdempotencyKey,
                RecordActorType.System,
                null,
                "Seed savings contribution");
            database.Transactions.Add(CreateTransaction(
                user.Id,
                checking.Id,
                -600.00m,
                "Savings",
                "Internal Transfer",
                "Savings contribution",
                TransactionState.Posted,
                savingsCorrelationId,
                savingsIdempotencyKey,
                RecordActorType.System,
                null));
            database.Transactions.Add(CreateTransaction(
                user.Id,
                savings.Id,
                600.00m,
                "Savings",
                "Internal Transfer",
                "Savings contribution",
                TransactionState.Posted,
                savingsCorrelationId,
                savingsIdempotencyKey,
                RecordActorType.System,
                null));

            database.Budgets.Add(new BudgetRule
            {
                UserId = user.Id,
                Category = "Dining",
                MonthlyLimit = 450m
            });
            database.Budgets.Add(new BudgetRule
            {
                UserId = user.Id,
                Category = "Groceries",
                MonthlyLimit = 650m
            });

            database.Goals.Add(new SavingsGoal
            {
                UserId = user.Id,
                SavingsAccountId = savings.Id,
                Name = "Laptop Fund",
                TargetAmount = 1200m,
                CurrentAmount = 420m
            });

            database.Notifications.Add(new NotificationItem
            {
                UserId = user.Id,
                Severity = NotificationSeverity.Info,
                Message = "Welcome to ConsoleATM Digital Banking."
            });

            database.AuditLogs.Add(new AuditLogEntry
            {
                ActorUserId = admin.Id,
                ActorRole = UserRole.Admin,
                Action = "seed_data",
                EntityType = "system",
                EntityId = "bootstrap",
                Metadata = "Initial seed completed."
            });

            RefreshLedgerState(database);
        }, cancellationToken);
    }

    public async Task<OperationResult> SignupAsync(SignupRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.Password) ||
            string.IsNullOrWhiteSpace(request.FirstName) ||
            string.IsNullOrWhiteSpace(request.LastName))
        {
            return OperationResult.Fail("Required fields are missing.");
        }

        string normalizedEmail = request.Email.Trim().ToLowerInvariant();
        if (!LooksLikeEmail(normalizedEmail))
        {
            return OperationResult.Fail("Email format is invalid.");
        }

        bool existingLocalUser = await _dataStore.ReadAsync(
            database => database.Users.Any(user => user.Email == normalizedEmail),
            cancellationToken);
        if (existingLocalUser)
        {
            return OperationResult.Fail("Email is already registered.");
        }

        if (_firebaseAuthClient.Enabled)
        {
            FirebaseAuthResult firebaseSignup = await _firebaseAuthClient.SignUpAsync(
                normalizedEmail,
                request.Password.Trim(),
                cancellationToken);

            if (!firebaseSignup.IsSuccess)
            {
                if (firebaseSignup.Message.Contains("EMAIL_EXISTS", StringComparison.OrdinalIgnoreCase))
                {
                    return OperationResult.Fail("Email is already registered.");
                }

                return OperationResult.Fail($"Firebase signup failed: {firebaseSignup.Message}");
            }
        }

        return await _dataStore.WriteAsync(database =>
        {
            if (database.Users.Any(user => user.Email == normalizedEmail))
            {
                return OperationResult.Fail("Email is already registered.");
            }

            var (hash, salt) = _securityService.HashPassword(request.Password.Trim());
            BankUser user = new()
            {
                Email = normalizedEmail,
                PasswordHash = hash,
                PasswordSalt = salt,
                FirstName = request.FirstName.Trim(),
                LastName = request.LastName.Trim(),
                Address = request.Address?.Trim() ?? string.Empty,
                Phone = request.Phone?.Trim() ?? string.Empty,
                Role = UserRole.User
            };

            BankAccount checking = CreateAccount(
                user.Id,
                BankAccountType.Checking,
                "Everyday Checking");
            BankAccount savings = CreateAccount(
                user.Id,
                BankAccountType.Savings,
                "Primary Savings");

            database.Users.Add(user);
            database.Accounts.Add(checking);
            database.Accounts.Add(savings);

            string openingCorrelationId = GenerateCorrelationId();
            AppendOpeningBalanceEntries(
                database,
                checking,
                250m,
                openingCorrelationId,
                $"signup-{user.Id:N}-checking");

            database.Notifications.Add(new NotificationItem
            {
                UserId = user.Id,
                Severity = NotificationSeverity.Success,
                Message = "Your new accounts are ready."
            });
            AddAudit(database, user.Id, UserRole.User, "signup", "user", user.Id.ToString("N"), "New signup");
            RefreshLedgerState(database);

            return OperationResult.Ok("Account created. Continue to login.");
        }, cancellationToken);
    }

    public async Task<LoginCompleteResult> LoginAsync(
        string email,
        string password,
        string deviceId,
        string deviceName,
        string ipAddress,
        string userAgent,
        CancellationToken cancellationToken = default)
    {
        string normalizedEmail = (email ?? string.Empty).Trim().ToLowerInvariant();
        string normalizedPassword = (password ?? string.Empty).Trim();
        FirebaseAuthResult? firebaseLoginResult = null;

        if (_firebaseAuthClient.Enabled)
        {
            firebaseLoginResult = await _firebaseAuthClient.SignInAsync(
                normalizedEmail,
                normalizedPassword,
                cancellationToken);
        }

        return await _dataStore.WriteAsync(database =>
        {
            CleanupExpiredRows(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;

            BankUser? user = database.Users.FirstOrDefault(candidate => candidate.Email == normalizedEmail);
            if (user is null)
            {
                return LoginCompleteResult.Fail("Invalid credentials.");
            }

            if (user.LockedUntilUtc.HasValue && user.LockedUntilUtc.Value > now)
            {
                return LoginCompleteResult.Fail($"Account locked until {user.LockedUntilUtc.Value.LocalDateTime:yyyy-MM-dd HH:mm}.");
            }

            bool localPasswordValid = _securityService.VerifyPassword(normalizedPassword, user.PasswordHash, user.PasswordSalt);
            bool firebasePasswordValid = !_firebaseAuthClient.Enabled || (firebaseLoginResult?.IsSuccess ?? false);
            bool credentialsValid = firebasePasswordValid || localPasswordValid;

            if (!credentialsValid)
            {
                user.FailedLoginAttempts += 1;
                if (user.FailedLoginAttempts >= BankingSecurityService.LockoutThreshold)
                {
                    user.FailedLoginAttempts = 0;
                    user.LockedUntilUtc = now.Add(BankingSecurityService.LockoutDuration);
                    AddAudit(database, user.Id, user.Role, "login_lockout", "user", user.Id.ToString("N"), "Too many invalid password attempts.");
                    return LoginCompleteResult.Fail($"Too many attempts. Account locked for {(int)BankingSecurityService.LockoutDuration.TotalMinutes} minutes.");
                }

                return LoginCompleteResult.Fail("Invalid credentials.");
            }

            if (_firebaseAuthClient.Enabled && !(firebaseLoginResult?.IsSuccess ?? false))
            {
                AddAudit(
                    database,
                    user.Id,
                    user.Role,
                    "login_local_fallback",
                    "user",
                    user.Id.ToString("N"),
                    "Firebase auth failed; local password fallback accepted.");
            }

            return CompleteLoginSuccess(
                database,
                user,
                now,
                deviceId,
                deviceName,
                ipAddress,
                userAgent);
        }, cancellationToken);
    }

    public async Task<RefreshResult> RefreshTokenAsync(
        string refreshToken,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return RefreshResult.Fail("Refresh token missing.");
        }

        return await _dataStore.WriteAsync(database =>
        {
            CleanupExpiredRows(database);

            BankUser? user = database.Users.FirstOrDefault(candidate =>
                candidate.RefreshSessions.Any(session =>
                    session.RefreshToken == refreshToken &&
                    session.ExpiresUtc > DateTimeOffset.UtcNow &&
                    session.DeviceId == deviceId));

            if (user is null)
            {
                return RefreshResult.Fail("Refresh token invalid.");
            }

            string newAccess = _securityService.IssueAccessToken(
                user.Id,
                user.Role,
                DateTimeOffset.UtcNow,
                BankingSecurityService.AccessTokenLifetime);
            string newRefresh = _securityService.GenerateRefreshToken();

            user.RefreshSessions.RemoveAll(session => session.RefreshToken == refreshToken);
            user.RefreshSessions.Add(new RefreshSession
            {
                RefreshToken = newRefresh,
                IssuedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.Add(BankingSecurityService.RefreshTokenLifetime),
                DeviceId = deviceId
            });

            return RefreshResult.Ok(newAccess, newRefresh);
        }, cancellationToken);
    }

    public async Task<BankUser?> GetUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await _dataStore.ReadAsync(
            database => database.Users.FirstOrDefault(user => user.Id == userId),
            cancellationToken);
    }

    public async Task<IReadOnlyList<BankAccount>> GetAccountsAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await _dataStore.ReadAsync(database =>
        {
            RefreshLedgerState(database);
            return database.Accounts
                .Where(account => account.UserId == userId)
                .OrderBy(account => account.Type)
                .ThenBy(account => account.Nickname)
                .ToList();
        }, cancellationToken);
    }

    public async Task<DashboardSnapshot?> GetDashboardSnapshotAsync(
        Guid userId,
        int daysWindow,
        CancellationToken cancellationToken = default)
    {
        int days = NormalizeDays(daysWindow);

        return await _dataStore.ReadAsync(database =>
        {
            RefreshLedgerState(database);
            BankUser? user = database.Users.FirstOrDefault(candidate => candidate.Id == userId);
            if (user is null)
            {
                return null;
            }

            List<BankAccount> accounts = database.Accounts
                .Where(account => account.UserId == userId)
                .OrderBy(account => account.Type)
                .ToList();

            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset windowStart = now.AddDays(-days);
            DateTimeOffset monthStart = new(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
            DateTimeOffset lastMonthStart = monthStart.AddMonths(-1);
            DateTimeOffset lastMonthEnd = monthStart.AddTicks(-1);

            List<BankTransaction> userTransactions = database.Transactions
                .Where(transaction => transaction.UserId == userId)
                .OrderByDescending(transaction => transaction.CreatedUtc)
                .ToList();
            List<BankTransaction> windowTransactions = userTransactions
                .Where(transaction => transaction.CreatedUtc >= windowStart)
                .ToList();
            List<BankTransaction> monthTransactions = userTransactions
                .Where(transaction => transaction.CreatedUtc >= monthStart && transaction.CreatedUtc <= now)
                .ToList();
            List<BankTransaction> lastMonthTransactions = userTransactions
                .Where(transaction => transaction.CreatedUtc >= lastMonthStart && transaction.CreatedUtc <= lastMonthEnd)
                .ToList();

            decimal inflow = monthTransactions.Where(transaction => transaction.Amount > 0m).Sum(transaction => transaction.Amount);
            decimal outflow = monthTransactions.Where(transaction => transaction.Amount < 0m).Sum(transaction => Math.Abs(transaction.Amount));
            decimal lastOutflow = lastMonthTransactions.Where(transaction => transaction.Amount < 0m).Sum(transaction => Math.Abs(transaction.Amount));

            List<CategorySpendItem> categories = monthTransactions
                .Where(transaction => transaction.Amount < 0m && transaction.State == TransactionState.Posted)
                .GroupBy(transaction => string.IsNullOrWhiteSpace(transaction.Category) ? "General" : transaction.Category)
                .Select(group => new CategorySpendItem(group.Key, Math.Abs(group.Sum(transaction => transaction.Amount))))
                .OrderByDescending(item => item.Amount)
                .ToList();

            List<BudgetProgressItem> budgets = database.Budgets
                .Where(budget => budget.UserId == userId)
                .Select(budget =>
                {
                    decimal spent = Math.Abs(monthTransactions
                        .Where(transaction => transaction.Amount < 0m && transaction.Category == budget.Category && transaction.State == TransactionState.Posted)
                        .Sum(transaction => transaction.Amount));
                    decimal percent = budget.MonthlyLimit <= 0m ? 0m : Math.Min(100m, decimal.Round((spent / budget.MonthlyLimit) * 100m, 1));
                    return new BudgetProgressItem(budget.Id, budget.Category, budget.MonthlyLimit, spent, percent);
                })
                .OrderByDescending(item => item.PercentUsed)
                .ToList();

            List<SavingsGoalProgressItem> goals = database.Goals
                .Where(goal => goal.UserId == userId)
                .Select(goal =>
                {
                    decimal percent = goal.TargetAmount <= 0m ? 0m : Math.Min(100m, decimal.Round((goal.CurrentAmount / goal.TargetAmount) * 100m, 1));
                    return new SavingsGoalProgressItem(goal.Id, goal.Name, goal.TargetAmount, goal.CurrentAmount, percent);
                })
                .OrderByDescending(goal => goal.PercentReached)
                .ToList();

            List<NotificationItem> notifications = database.Notifications
                .Where(notification => notification.UserId == userId)
                .OrderByDescending(notification => notification.CreatedUtc)
                .Take(20)
                .ToList();

            List<BankTransfer> upcomingTransfers = database.Transfers
                .Where(transfer =>
                    transfer.UserId == userId &&
                    transfer.State == TransferState.Scheduled &&
                    transfer.NextRunUtc.HasValue)
                .OrderBy(transfer => transfer.NextRunUtc)
                .Take(10)
                .ToList();

            decimal totalBalance = accounts.Sum(account => account.AvailableBalance);

            return new DashboardSnapshot(
                user,
                accounts,
                windowTransactions.Take(100).ToList(),
                inflow,
                outflow,
                lastOutflow,
                totalBalance,
                categories,
                budgets,
                goals,
                notifications,
                upcomingTransfers,
                days);
        }, cancellationToken);
    }

    public async Task<LedgerPageResult?> GetLedgerAsync(
        Guid userId,
        LedgerFilter filter,
        CancellationToken cancellationToken = default)
    {
        return await _dataStore.ReadAsync(database =>
        {
            RefreshLedgerState(database);
            BankUser? user = database.Users.FirstOrDefault(candidate => candidate.Id == userId);
            if (user is null)
            {
                return null;
            }

            IEnumerable<BankTransaction> query = database.Transactions
                .Where(transaction => transaction.UserId == userId);

            if (filter.AccountId.HasValue)
            {
                query = query.Where(transaction => transaction.AccountId == filter.AccountId.Value);
            }

            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                string search = filter.Search.Trim().ToLowerInvariant();
                query = query.Where(transaction =>
                    transaction.MerchantName.ToLowerInvariant().Contains(search) ||
                    transaction.Category.ToLowerInvariant().Contains(search) ||
                    transaction.Description.ToLowerInvariant().Contains(search) ||
                    transaction.ReferenceCode.ToLowerInvariant().Contains(search));
            }

            if (!string.IsNullOrWhiteSpace(filter.Category))
            {
                string category = filter.Category.Trim();
                query = query.Where(transaction => transaction.Category == category);
            }

            if (filter.State.HasValue)
            {
                query = query.Where(transaction => transaction.State == filter.State.Value);
            }

            if (filter.FromUtc.HasValue)
            {
                query = query.Where(transaction => transaction.CreatedUtc >= filter.FromUtc.Value);
            }

            if (filter.ToUtc.HasValue)
            {
                query = query.Where(transaction => transaction.CreatedUtc <= filter.ToUtc.Value);
            }

            List<BankTransaction> items = query
                .OrderByDescending(transaction => transaction.CreatedUtc)
                .Take(400)
                .ToList();

            List<string> categories = database.Transactions
                .Where(transaction => transaction.UserId == userId)
                .Select(transaction => transaction.Category)
                .Distinct()
                .OrderBy(category => category)
                .ToList();

            List<BankAccount> accounts = database.Accounts
                .Where(account => account.UserId == userId)
                .OrderBy(account => account.Type)
                .ToList();

            return new LedgerPageResult(items, categories, accounts);
        }, cancellationToken);
    }

    public async Task<BankTransaction?> GetTransactionAsync(
        Guid userId,
        Guid transactionId,
        CancellationToken cancellationToken = default)
    {
        return await _dataStore.ReadAsync(
            database => database.Transactions.FirstOrDefault(transaction =>
                transaction.Id == transactionId && transaction.UserId == userId),
            cancellationToken);
    }

    public async Task<OperationResult> SaveReceiptNoteAsync(
        Guid userId,
        Guid transactionId,
        string note,
        CancellationToken cancellationToken = default)
    {
        return await _dataStore.WriteAsync(database =>
        {
            BankTransaction? transaction = database.Transactions.FirstOrDefault(candidate =>
                candidate.Id == transactionId && candidate.UserId == userId);
            if (transaction is null)
            {
                return OperationResult.Fail("Transaction not found.");
            }

            transaction.ReceiptNote = (note ?? string.Empty).Trim();
            AddAudit(database, userId, UserRole.User, "update_receipt_note", "transaction", transactionId.ToString("N"), "Receipt note updated");
            return OperationResult.Ok("Receipt note saved.");
        }, cancellationToken);
    }

    public async Task<OperationResult> SaveProfileAsync(
        Guid userId,
        string firstName,
        string lastName,
        string address,
        string phone,
        CancellationToken cancellationToken = default)
    {
        return await _dataStore.WriteAsync(database =>
        {
            BankUser? user = database.Users.FirstOrDefault(candidate => candidate.Id == userId);
            if (user is null)
            {
                return OperationResult.Fail("User not found.");
            }

            if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
            {
                return OperationResult.Fail("First and last name are required.");
            }

            user.FirstName = firstName.Trim();
            user.LastName = lastName.Trim();
            user.Address = (address ?? string.Empty).Trim();
            user.Phone = (phone ?? string.Empty).Trim();

            AddAudit(database, userId, user.Role, "update_profile", "user", userId.ToString("N"), "Profile updated");
            return OperationResult.Ok("Profile updated.");
        }, cancellationToken);
    }

    public async Task<OperationResult> UpdateSettingsAsync(
        Guid userId,
        bool emailNotificationsEnabled,
        bool securityAlertsEnabled,
        bool marketingEmailsEnabled,
        CancellationToken cancellationToken = default)
    {
        return await _dataStore.WriteAsync(database =>
        {
            BankUser? user = database.Users.FirstOrDefault(candidate => candidate.Id == userId);
            if (user is null)
            {
                return OperationResult.Fail("User not found.");
            }

            user.Settings.EmailNotificationsEnabled = emailNotificationsEnabled;
            user.Settings.SecurityAlertsEnabled = securityAlertsEnabled;
            user.Settings.MarketingEmailsEnabled = marketingEmailsEnabled;
            AddAudit(database, userId, user.Role, "update_settings", "user", userId.ToString("N"), "Settings updated");
            return OperationResult.Ok("Settings saved.");
        }, cancellationToken);
    }

    public async Task<OperationResult> ToggleFreezeAccountAsync(
        Guid actorUserId,
        UserRole actorRole,
        Guid accountId,
        bool freeze,
        CancellationToken cancellationToken = default)
    {
        return await _dataStore.WriteAsync(database =>
        {
            BankAccount? account = database.Accounts.FirstOrDefault(candidate => candidate.Id == accountId);
            if (account is null)
            {
                return OperationResult.Fail("Account not found.");
            }

            if (actorRole != UserRole.Admin && account.UserId != actorUserId)
            {
                return OperationResult.Fail("Not authorized.");
            }

            account.Status = freeze ? BankAccountStatus.Frozen : BankAccountStatus.Active;
            AddAudit(database, actorUserId, actorRole, freeze ? "freeze_account" : "unfreeze_account", "account", accountId.ToString("N"), $"Status={account.Status}");
            AddDomainEvent(
                database,
                freeze ? "AccountFrozen" : "AccountUnfrozen",
                "account",
                accountId.ToString("N"),
                account.UserId,
                accountId.ToString("N"),
                $"Status={account.Status};ActorRole={actorRole}");
            _realtimeQueue.EnqueueUser(account.UserId, "notification", new
            {
                severity = freeze ? "warning" : "info",
                title = "Account status",
                message = freeze
                    ? $"Account {MaskAccountNumber(account.AccountNumber)} was frozen."
                    : $"Account {MaskAccountNumber(account.AccountNumber)} was reactivated."
            });

            if (freeze)
            {
                database.Notifications.Add(new NotificationItem
                {
                    UserId = account.UserId,
                    Severity = NotificationSeverity.Warning,
                    Message = $"Account {MaskAccountNumber(account.AccountNumber)} was frozen."
                });
            }

            return OperationResult.Ok(freeze ? "Account frozen." : "Account unfrozen.");
        }, cancellationToken);
    }

    public async Task<AccountOpenResult> OpenAccountAsync(
        Guid userId,
        BankAccountType accountType,
        string nickname,
        Guid? fundingSourceAccountId,
        decimal openingDeposit,
        CancellationToken cancellationToken = default)
    {
        using Activity? activity = _telemetry.ActivitySource.StartActivity(
            "banking.account.open",
            ActivityKind.Internal);
        activity?.SetTag("bank.user_id", userId.ToString("N"));
        activity?.SetTag("bank.account.type", accountType.ToString());
        activity?.SetTag("bank.account.opening_deposit", openingDeposit);

        if (accountType == BankAccountType.ExternalLinked)
        {
            return AccountOpenResult.Fail("Use linked account flow for external accounts.");
        }

        decimal normalizedDeposit = decimal.Round(openingDeposit, 2, MidpointRounding.AwayFromZero);
        if (normalizedDeposit < 0m)
        {
            return AccountOpenResult.Fail("Opening deposit cannot be negative.");
        }

        string trimmedNickname = (nickname ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmedNickname))
        {
            return AccountOpenResult.Fail("Account nickname is required.");
        }

        AccountOpenResult result = await _dataStore.WriteAsync(database =>
        {
            RefreshLedgerState(database);

            BankUser? user = database.Users.FirstOrDefault(candidate => candidate.Id == userId);
            if (user is null)
            {
                return AccountOpenResult.Fail("User not found.");
            }

            bool nicknameExists = database.Accounts.Any(account =>
                account.UserId == userId &&
                !account.IsSystemAccount &&
                string.Equals(account.Nickname, trimmedNickname, StringComparison.OrdinalIgnoreCase));
            if (nicknameExists)
            {
                return AccountOpenResult.Fail("You already have an account with that nickname.");
            }

            BankAccount? fundingSource = null;
            if (normalizedDeposit > 0m)
            {
                if (!fundingSourceAccountId.HasValue)
                {
                    return AccountOpenResult.Fail("Select a funding source for the opening deposit.");
                }

                fundingSource = database.Accounts.FirstOrDefault(account =>
                    account.Id == fundingSourceAccountId.Value &&
                    account.UserId == userId);
                if (fundingSource is null)
                {
                    return AccountOpenResult.Fail("Funding source account was not found.");
                }

                if (fundingSource.Type == BankAccountType.ExternalLinked)
                {
                    return AccountOpenResult.Fail("Linked external accounts cannot fund new accounts directly.");
                }

                if (fundingSource.Status == BankAccountStatus.Frozen)
                {
                    return AccountOpenResult.Fail("Funding source account is frozen.");
                }

                if (normalizedDeposit > fundingSource.AvailableBalance)
                {
                    return AccountOpenResult.Fail("Insufficient available funds for opening deposit.");
                }
            }

            BankAccount createdAccount = CreateAccount(userId, accountType, trimmedNickname);
            database.Accounts.Add(createdAccount);

            if (normalizedDeposit > 0m && fundingSource is not null)
            {
                string correlationId = GenerateCorrelationId();
                string idempotencyKey = $"account-open-{createdAccount.Id:N}";
                string description = $"Initial funding for {createdAccount.Nickname}";

                AppendBalancedLedger(
                    database,
                    fundingSource,
                    createdAccount,
                    normalizedDeposit,
                    normalizedDeposit,
                    "account_opening_funding",
                    correlationId,
                    idempotencyKey,
                    RecordActorType.User,
                    userId,
                    description);

                database.Transactions.Add(CreateTransaction(
                    userId,
                    fundingSource.Id,
                    -normalizedDeposit,
                    "Transfer",
                    "Internal Transfer",
                    description,
                    TransactionState.Posted,
                    correlationId,
                    idempotencyKey,
                    RecordActorType.User,
                    userId));

                database.Transactions.Add(CreateTransaction(
                    userId,
                    createdAccount.Id,
                    normalizedDeposit,
                    "Transfer",
                    "Internal Transfer",
                    $"Initial funding from {fundingSource.Nickname}",
                    TransactionState.Posted,
                    correlationId,
                    idempotencyKey,
                    RecordActorType.User,
                    userId));
            }

            RefreshLedgerState(database);

            database.Notifications.Add(new NotificationItem
            {
                UserId = userId,
                Severity = NotificationSeverity.Success,
                Message = normalizedDeposit > 0m
                    ? $"New {accountType} account opened with {normalizedDeposit:C}."
                    : $"New {accountType} account opened."
            });

            AddAudit(
                database,
                userId,
                user.Role,
                "open_account",
                "account",
                createdAccount.Id.ToString("N"),
                $"Type={accountType};Nickname={trimmedNickname};OpeningDeposit={normalizedDeposit}");
            AddDomainEvent(
                database,
                "AccountOpened",
                "account",
                createdAccount.Id.ToString("N"),
                userId,
                createdAccount.Id.ToString("N"),
                $"Type={accountType};OpeningDeposit={normalizedDeposit}");

            QueueAccountBalance(database, createdAccount, "New account opened.");
            if (fundingSource is not null && normalizedDeposit > 0m)
            {
                QueueAccountBalance(database, fundingSource, "Funding source balance updated.");
                CreateLowBalanceAlertIfNeeded(database, fundingSource);
            }

            return AccountOpenResult.Ok(
                createdAccount.Id,
                normalizedDeposit > 0m
                    ? "Account opened with initial funding."
                    : "Account opened.");
        }, cancellationToken);

        activity?.SetTag("account.open.success", result.IsSuccess);
        if (result.AccountId.HasValue)
        {
            activity?.SetTag("bank.account_id", result.AccountId.Value.ToString("N"));
        }

        if (!result.IsSuccess)
        {
            activity?.SetStatus(ActivityStatusCode.Error, result.Message);
            _logger.LogWarning(
                "Account opening failed. UserId={UserId} AccountType={AccountType} OpeningDeposit={OpeningDeposit} Message={Message}",
                userId,
                accountType,
                openingDeposit,
                result.Message);
        }
        else
        {
            _logger.LogInformation(
                "Account opened. UserId={UserId} AccountId={AccountId} AccountType={AccountType} OpeningDeposit={OpeningDeposit}",
                userId,
                result.AccountId,
                accountType,
                openingDeposit);
        }

        return result;
    }

    public async Task<OperationResult> LinkExternalAccountAsync(
        Guid userId,
        string bankName,
        string nickname,
        string accountMask,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bankName) || string.IsNullOrWhiteSpace(accountMask))
        {
            return OperationResult.Fail("Bank name and account mask are required.");
        }

        return await _dataStore.WriteAsync(database =>
        {
            if (!database.Users.Any(user => user.Id == userId))
            {
                return OperationResult.Fail("User not found.");
            }

            LinkedExternalAccount account = new()
            {
                UserId = userId,
                BankName = bankName.Trim(),
                Nickname = string.IsNullOrWhiteSpace(nickname) ? "Linked Account" : nickname.Trim(),
                AccountMask = accountMask.Trim(),
                RoutingNumber = CreateRoutingNumber()
            };

            database.LinkedExternalAccounts.Add(account);
            AddAudit(database, userId, UserRole.User, "link_external_account", "external_account", account.Id.ToString("N"), $"{account.BankName} {account.AccountMask}");
            database.Notifications.Add(new NotificationItem
            {
                UserId = userId,
                Severity = NotificationSeverity.Success,
                Message = $"External account linked: {account.BankName} {account.AccountMask}"
            });

            return OperationResult.Ok("External account linked.");
        }, cancellationToken);
    }

    public async Task<TransferActionResult> CreateTransferAsync(
        Guid userId,
        TransferRequest request,
        UserRole actorRole,
        CancellationToken cancellationToken = default)
    {
        string transferKind = request.DestinationExternalAccountId.HasValue ? "external_ach" : "internal";
        using Activity? activity = _telemetry.ActivitySource.StartActivity(
            "banking.transfer.create",
            ActivityKind.Internal);
        activity?.SetTag("bank.user_id", userId.ToString("N"));
        activity?.SetTag("transfer.kind", transferKind);
        activity?.SetTag("transfer.amount", request.Amount);
        activity?.SetTag("bank.account.source_id", request.SourceAccountId.ToString("N"));

        TransferActionResult result = await _dataStore.WriteAsync(database =>
        {
            CleanupExpiredRows(database);

            BankUser? user = database.Users.FirstOrDefault(candidate => candidate.Id == userId);
            if (user is null)
            {
                return TransferActionResult.Fail("User not found.");
            }

            if (request.Amount <= 0m)
            {
                return TransferActionResult.Fail("Amount must be greater than 0.");
            }

            string idempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey)
                ? Guid.NewGuid().ToString("N")
                : request.IdempotencyKey.Trim();

            IdempotencyRecord? existingIdempotency = database.IdempotencyKeys
                .FirstOrDefault(record => record.Key == idempotencyKey);
            if (existingIdempotency is not null)
            {
                BankTransfer? existingTransfer = database.Transfers.FirstOrDefault(transfer => transfer.Id == existingIdempotency.TransferId);
                if (existingTransfer is not null)
                {
                    return TransferActionResult.Ok(existingTransfer.Id, existingTransfer.State, "Duplicate request ignored via idempotency key.");
                }
            }

            BankAccount? source = database.Accounts.FirstOrDefault(account =>
                account.Id == request.SourceAccountId &&
                account.UserId == userId);
            if (source is null)
            {
                return TransferActionResult.Fail("Source account was not found.");
            }

            if (source.Status == BankAccountStatus.Frozen)
            {
                return TransferActionResult.Fail("Source account is frozen.");
            }

            decimal amount = decimal.Round(request.Amount, 2, MidpointRounding.AwayFromZero);
            if (amount > source.AvailableBalance)
            {
                return TransferActionResult.Fail("Insufficient available funds.");
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset scheduledFor = request.ScheduledForUtc ?? now;
            bool isScheduled = scheduledFor > now.AddMinutes(1) || request.Frequency != TransferFrequency.OneTime;
            bool isExternal = request.DestinationExternalAccountId.HasValue;
            string correlationId = GenerateCorrelationId();
            RecordActorType createdBy = ToRecordActor(actorRole);

            BankTransfer transfer = new()
            {
                UserId = userId,
                SourceAccountId = source.Id,
                DestinationInternalAccountId = request.DestinationInternalAccountId,
                DestinationExternalAccountId = request.DestinationExternalAccountId,
                Amount = amount,
                Memo = (request.Memo ?? string.Empty).Trim(),
                IsExternalAch = isExternal,
                Frequency = request.Frequency,
                ScheduledUtc = scheduledFor,
                NextRunUtc = isScheduled ? scheduledFor : null,
                RequestedUtc = now,
                CorrelationId = correlationId,
                IdempotencyKey = idempotencyKey,
                State = isScheduled ? TransferState.Scheduled : (isExternal ? TransferState.Pending : TransferState.Posted),
                CreatedBy = createdBy,
                CreatedByUserId = userId
            };

            FraudAssessment fraudAssessment = AssessTransferRisk(
                database,
                user,
                source,
                amount,
                isExternal,
                request.DestinationExternalAccountId,
                now);
            transfer.FraudScore = fraudAssessment.Score;
            transfer.FraudFlags = fraudAssessment.Flags;
            if (fraudAssessment.ShouldReview)
            {
                transfer.State = TransferState.UnderReview;
                transfer.ReviewNotes = "Queued for fraud review.";
            }

            database.Transfers.Add(transfer);
            database.IdempotencyKeys.Add(new IdempotencyRecord
            {
                Key = idempotencyKey,
                TransferId = transfer.Id
            });
            AddDomainEvent(
                database,
                "TransferCreated",
                "transfer",
                transfer.Id.ToString("N"),
                userId,
                correlationId,
                $"State={transfer.State};Amount={amount};External={isExternal};Scheduled={isScheduled}");

            if (fraudAssessment.ShouldReview)
            {
                database.Notifications.Add(new NotificationItem
                {
                    UserId = userId,
                    Severity = NotificationSeverity.Warning,
                    Message = $"Transfer {transfer.Id.ToString("N")[..8]} is under review for fraud checks."
                });
                AddAudit(
                    database,
                    userId,
                    actorRole,
                    "transfer_under_review",
                    "transfer",
                    transfer.Id.ToString("N"),
                    $"FraudScore={fraudAssessment.Score};Flags={fraudAssessment.Flags}");
                QueueTransferStatus(
                    transfer,
                    "Transfer submitted for fraud review.",
                    isWarning: true);
                _realtimeQueue.EnqueueAdmins("fraud.alert", new
                {
                    transferId = transfer.Id.ToString("N"),
                    userId = transfer.UserId.ToString("N"),
                    amount = transfer.Amount,
                    fraudScore = transfer.FraudScore,
                    fraudFlags = transfer.FraudFlags,
                    message = $"Transfer {transfer.Id.ToString("N")[..8]} requires manual review."
                });
                AddDomainEvent(
                    database,
                    "TransferFlaggedForReview",
                    "transfer",
                    transfer.Id.ToString("N"),
                    userId,
                    correlationId,
                    fraudAssessment.Flags);
                return TransferActionResult.Ok(transfer.Id, transfer.State, "Transfer submitted for manual review.");
            }

            if (isScheduled)
            {
                database.Notifications.Add(new NotificationItem
                {
                    UserId = userId,
                    Severity = NotificationSeverity.Info,
                    Message = $"Transfer scheduled for {scheduledFor.LocalDateTime:g}."
                });
                AddAudit(database, userId, actorRole, "schedule_transfer", "transfer", transfer.Id.ToString("N"), $"Amount={amount}");
                QueueTransferStatus(transfer, "Transfer scheduled.");
                AddDomainEvent(
                    database,
                    "TransferScheduled",
                    "transfer",
                    transfer.Id.ToString("N"),
                    userId,
                    correlationId,
                    $"Amount={amount};Frequency={transfer.Frequency}");
                return TransferActionResult.Ok(transfer.Id, transfer.State, "Transfer scheduled.");
            }

            if (isExternal)
            {
                transfer.SettlesUtc = now.AddMinutes(2);
                transfer.State = TransferState.Pending;

                AppendBalancedLedger(
                    database,
                    source,
                    GetOrCreateSystemAccount(database, SystemSettlementAccountKey, "ACH Settlement Clearing"),
                    amount,
                    0m,
                    "external_transfer_hold",
                    correlationId,
                    idempotencyKey,
                    createdBy,
                    userId,
                    BuildTransferDescription(transfer, database),
                    transfer.Id);

                database.Transactions.Add(CreateTransaction(
                    userId,
                    source.Id,
                    -amount,
                    "Transfer",
                    "ACH External",
                    BuildTransferDescription(transfer, database),
                    TransactionState.Pending,
                    correlationId,
                    idempotencyKey,
                    createdBy,
                    userId,
                    transfer.Id));

                database.Notifications.Add(new NotificationItem
                {
                    UserId = userId,
                    Severity = NotificationSeverity.Info,
                    Message = $"ACH transfer submitted and pending settlement ({amount:C})."
                });

                AddAudit(database, userId, actorRole, "create_external_transfer", "transfer", transfer.Id.ToString("N"), $"Amount={amount}");
                AddDomainEvent(
                    database,
                    "TransferSubmitted",
                    "transfer",
                    transfer.Id.ToString("N"),
                    userId,
                    correlationId,
                    "External ACH transfer pending settlement");
                RefreshLedgerState(database);
                CreateLowBalanceAlertIfNeeded(database, source);
                QueueTransferStatus(transfer, "ACH transfer is pending settlement.");
                QueueAccountBalance(database, source, "Available balance updated for ACH hold.");
                return TransferActionResult.Ok(transfer.Id, transfer.State, "External ACH transfer is pending.");
            }

            if (!request.DestinationInternalAccountId.HasValue)
            {
                return TransferActionResult.Fail("Destination account is required.");
            }

            BankAccount? destination = database.Accounts.FirstOrDefault(account =>
                account.Id == request.DestinationInternalAccountId.Value &&
                !account.IsSystemAccount &&
                account.Type != BankAccountType.ExternalLinked);
            if (destination is null)
            {
                return TransferActionResult.Fail("Destination account was not found.");
            }

            if (destination.Status == BankAccountStatus.Frozen)
            {
                return TransferActionResult.Fail("Destination account is frozen.");
            }

            if (destination.Id == source.Id)
            {
                return TransferActionResult.Fail("Source and destination accounts must be different.");
            }

            bool isPeerTransfer = destination.UserId != userId;
            BankUser? destinationOwner = database.Users.FirstOrDefault(candidate => candidate.Id == destination.UserId);
            string destinationOwnerName = destinationOwner is null ? "recipient" : BuildDisplayName(destinationOwner);
            string senderDisplayName = BuildDisplayName(user);

            transfer.State = TransferState.Posted;
            transfer.SettlesUtc = now;

            AppendBalancedLedger(
                database,
                source,
                destination,
                amount,
                amount,
                "internal_transfer_posted",
                correlationId,
                idempotencyKey,
                createdBy,
                userId,
                BuildTransferDescription(transfer, database),
                    transfer.Id);

            database.Transactions.Add(CreateTransaction(
                userId,
                source.Id,
                -amount,
                "Transfer",
                isPeerTransfer ? "P2P Transfer" : "Internal Transfer",
                isPeerTransfer
                    ? $"Transfer to {destinationOwnerName} ({destination.Nickname})"
                    : BuildTransferDescription(transfer, database),
                TransactionState.Posted,
                correlationId,
                idempotencyKey,
                createdBy,
                userId,
                transfer.Id));
            database.Transactions.Add(CreateTransaction(
                destination.UserId,
                destination.Id,
                amount,
                "Transfer",
                isPeerTransfer ? "P2P Transfer" : "Internal Transfer",
                isPeerTransfer
                    ? $"Transfer from {senderDisplayName}"
                    : $"Transfer from {source.Nickname}",
                TransactionState.Posted,
                correlationId,
                idempotencyKey,
                createdBy,
                userId,
                transfer.Id));

            database.Notifications.Add(new NotificationItem
            {
                UserId = userId,
                Severity = NotificationSeverity.Success,
                Message = isPeerTransfer
                    ? $"Transfer sent to {destinationOwnerName} ({amount:C})."
                    : $"Internal transfer completed ({amount:C})."
            });
            if (isPeerTransfer)
            {
                database.Notifications.Add(new NotificationItem
                {
                    UserId = destination.UserId,
                    Severity = NotificationSeverity.Info,
                    Message = $"You received {amount:C} from {senderDisplayName}."
                });
                _realtimeQueue.EnqueueUser(destination.UserId, "notification", new
                {
                    severity = "info",
                    title = "Incoming transfer",
                    message = $"You received {amount:C} from {senderDisplayName}."
                });
            }

            AddAudit(
                database,
                userId,
                actorRole,
                isPeerTransfer ? "create_peer_transfer" : "create_internal_transfer",
                "transfer",
                transfer.Id.ToString("N"),
                $"Amount={amount};DestinationUserId={destination.UserId:N}");
            AddDomainEvent(
                database,
                "TransferPosted",
                "transfer",
                transfer.Id.ToString("N"),
                userId,
                correlationId,
                isPeerTransfer
                    ? $"Peer transfer posted immediately to {destination.UserId:N}"
                    : "Internal transfer posted immediately");
            RefreshLedgerState(database);
            CreateLowBalanceAlertIfNeeded(database, source);
            QueueTransferStatus(
                transfer,
                isPeerTransfer ? "Transfer posted to another user." : "Internal transfer posted.");
            QueueAccountBalance(database, source, "Source account balance updated.");
            QueueAccountBalance(database, destination, "Destination account balance updated.");
            return TransferActionResult.Ok(transfer.Id, transfer.State, "Transfer completed.");
        }, cancellationToken);

        _telemetry.RecordTransfer(transferKind, result.IsSuccess);
        activity?.SetTag("transfer.success", result.IsSuccess);
        activity?.SetTag("transfer.state", result.State?.ToString());
        if (!result.IsSuccess)
        {
            activity?.SetStatus(ActivityStatusCode.Error, result.Message);
            _logger.LogWarning(
                "Transfer request failed. UserId={UserId} Kind={TransferKind} SourceAccountId={SourceAccountId} Amount={Amount} Message={Message}",
                userId,
                transferKind,
                request.SourceAccountId,
                request.Amount,
                result.Message);
        }
        else
        {
            _logger.LogInformation(
                "Transfer request completed. UserId={UserId} Kind={TransferKind} SourceAccountId={SourceAccountId} Amount={Amount} TransferId={TransferId} State={State}",
                userId,
                transferKind,
                request.SourceAccountId,
                request.Amount,
                result.TransferId,
                result.State);
        }

        return result;
    }

    public async Task<int> RunSettlementAsync(CancellationToken cancellationToken = default)
    {
        using Activity? activity = _telemetry.ActivitySource.StartActivity(
            "banking.settlement.execute",
            ActivityKind.Internal);

        int processed = await _dataStore.WriteAsync(database =>
        {
            int processed = 0;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            CleanupExpiredRows(database);

            List<BankTransfer> dueScheduled = database.Transfers
                .Where(transfer =>
                    transfer.State == TransferState.Scheduled &&
                    transfer.NextRunUtc.HasValue &&
                    transfer.NextRunUtc.Value <= now)
                .ToList();

            foreach (BankTransfer scheduled in dueScheduled)
            {
                TransferRequest request = new()
                {
                    SourceAccountId = scheduled.SourceAccountId,
                    DestinationInternalAccountId = scheduled.DestinationInternalAccountId,
                    DestinationExternalAccountId = scheduled.DestinationExternalAccountId,
                    Amount = scheduled.Amount,
                    Memo = scheduled.Memo,
                    ScheduledForUtc = now,
                    Frequency = TransferFrequency.OneTime,
                    IdempotencyKey = $"{scheduled.IdempotencyKey}-run-{now:yyyyMMddHHmmss}"
                };

                TransferActionResult result = ExecuteTransferFromSchedule(database, scheduled, request, now);
                if (result.IsSuccess)
                {
                    processed++;
                }
            }

            List<BankTransfer> pendingExternal = database.Transfers
                .Where(transfer =>
                    transfer.State == TransferState.Pending &&
                    transfer.IsExternalAch &&
                    transfer.SettlesUtc.HasValue &&
                    transfer.SettlesUtc.Value <= now)
                .ToList();

            foreach (BankTransfer pending in pendingExternal)
            {
                pending.State = TransferState.Posted;
                pending.SettlesUtc = now;
                BankAccount? source = database.Accounts.FirstOrDefault(account => account.Id == pending.SourceAccountId);
                bool hasHoldEntry = database.LedgerEntries.Any(entry =>
                    entry.TransferId == pending.Id &&
                    entry.EntryType == "external_transfer_hold");
                if (source is not null && hasHoldEntry)
                {
                    string correlationId = string.IsNullOrWhiteSpace(pending.CorrelationId)
                        ? GenerateCorrelationId()
                        : pending.CorrelationId;
                    BankAccount settlementAccount = GetOrCreateSystemAccount(database, SystemSettlementAccountKey, "ACH Settlement Clearing");
                    string description = BuildTransferDescription(pending, database);

                    database.LedgerEntries.Add(new LedgerEntry
                    {
                        AccountId = source.Id,
                        UserId = source.UserId,
                        TransferId = pending.Id,
                        AvailableDelta = 0m,
                        LedgerDelta = decimal.Round(-pending.Amount, 2, MidpointRounding.AwayFromZero),
                        EntryType = "external_transfer_settlement",
                        Description = description,
                        CorrelationId = correlationId,
                        IdempotencyKey = pending.IdempotencyKey,
                        CreatedBy = RecordActorType.System
                    });
                    database.LedgerEntries.Add(new LedgerEntry
                    {
                        AccountId = settlementAccount.Id,
                        UserId = settlementAccount.UserId,
                        TransferId = pending.Id,
                        AvailableDelta = decimal.Round(-pending.Amount, 2, MidpointRounding.AwayFromZero),
                        LedgerDelta = decimal.Round(pending.Amount, 2, MidpointRounding.AwayFromZero),
                        EntryType = "external_transfer_settlement",
                        Description = description,
                        CorrelationId = correlationId,
                        IdempotencyKey = pending.IdempotencyKey,
                        CreatedBy = RecordActorType.System
                    });
                }

                BankTransaction? pendingTx = database.Transactions.FirstOrDefault(transaction =>
                    transaction.UserId == pending.UserId &&
                    transaction.AccountId == pending.SourceAccountId &&
                    transaction.State == TransactionState.Pending &&
                    transaction.Description.Contains(pending.Id.ToString("N")[..6], StringComparison.OrdinalIgnoreCase));
                if (pendingTx is not null)
                {
                    pendingTx.State = TransactionState.Posted;
                    pendingTx.PostedUtc = now;
                }

                database.Notifications.Add(new NotificationItem
                {
                    UserId = pending.UserId,
                    Severity = NotificationSeverity.Success,
                    Message = $"ACH transfer posted for {pending.Amount:C}."
                });

                AddAudit(database, pending.UserId, UserRole.User, "settle_external_transfer", "transfer", pending.Id.ToString("N"), "Background settlement posted transfer");
                AddDomainEvent(
                    database,
                    "TransferSettled",
                    "transfer",
                    pending.Id.ToString("N"),
                    pending.UserId,
                    pending.CorrelationId,
                    $"Amount={pending.Amount};External=true");
                RefreshLedgerState(database);
                QueueTransferStatus(pending, "ACH transfer posted.");
                if (source is not null)
                {
                    QueueAccountBalance(database, source, "Settlement posted and ledger balance updated.");
                }
                processed++;
            }

            GenerateMonthlyStatementRecords(database, now);
            return processed;
        }, cancellationToken);

        activity?.SetTag("settlement.processed_count", processed);
        if (processed > 0)
        {
            _logger.LogInformation("Settlement execution completed. ProcessedTransfers={ProcessedTransfers}", processed);
        }

        return processed;
    }

    public async Task<IReadOnlyList<LinkedExternalAccount>> GetLinkedExternalAccountsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await _dataStore.ReadAsync(
            database => database.LinkedExternalAccounts
                .Where(account => account.UserId == userId)
                .OrderByDescending(account => account.LinkedUtc)
                .ToList(),
            cancellationToken);
    }

    public async Task<IReadOnlyList<TransferRecipientAccount>> GetTransferRecipientsAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        return await _dataStore.ReadAsync(database =>
        {
            List<TransferRecipientAccount> recipients = (
                from account in database.Accounts
                join owner in database.Users on account.UserId equals owner.Id
                where account.UserId != userId &&
                      !account.IsSystemAccount &&
                      account.Type != BankAccountType.ExternalLinked &&
                      account.Status == BankAccountStatus.Active &&
                      owner.Role == UserRole.User
                orderby owner.Email, account.Type, account.Nickname
                select new TransferRecipientAccount(
                    account.Id,
                    owner.Id,
                    BuildDisplayName(owner),
                    owner.Email,
                    account.Nickname,
                    MaskAccountNumber(account.AccountNumber),
                    account.Type))
                .ToList();

            return recipients;
        }, cancellationToken);
    }

    public async Task<TransferActionResult> CreatePeerTransferByEmailAsync(
        Guid userId,
        PeerTransferByEmailRequest request,
        UserRole actorRole,
        CancellationToken cancellationToken = default)
    {
        string recipientEmail = (request.RecipientEmail ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(recipientEmail) || !LooksLikeEmail(recipientEmail))
        {
            return TransferActionResult.Fail("Recipient email is required.");
        }

        if (request.PreferredDestinationAccountType == BankAccountType.ExternalLinked)
        {
            return TransferActionResult.Fail("Recipient account type must be Checking or Savings.");
        }

        PeerDestinationResolution resolution = await _dataStore.ReadAsync(database =>
        {
            BankUser? recipient = database.Users.FirstOrDefault(candidate =>
                candidate.Role == UserRole.User &&
                candidate.Id != userId &&
                string.Equals(candidate.Email, recipientEmail, StringComparison.OrdinalIgnoreCase));
            if (recipient is null)
            {
                return PeerDestinationResolution.Fail("No recipient user found for that email.");
            }

            IEnumerable<BankAccount> query = database.Accounts.Where(account =>
                account.UserId == recipient.Id &&
                !account.IsSystemAccount &&
                account.Type != BankAccountType.ExternalLinked &&
                account.Status == BankAccountStatus.Active);

            if (request.PreferredDestinationAccountType.HasValue)
            {
                query = query.Where(account => account.Type == request.PreferredDestinationAccountType.Value);
            }

            BankAccount? destination = query
                .OrderBy(account => account.Type == BankAccountType.Checking ? 0 : 1)
                .ThenBy(account => account.CreatedUtc)
                .FirstOrDefault();

            if (destination is null)
            {
                return request.PreferredDestinationAccountType.HasValue
                    ? PeerDestinationResolution.Fail("Recipient does not have an active account for the selected type.")
                    : PeerDestinationResolution.Fail("Recipient has no active destination account.");
            }

            return PeerDestinationResolution.Ok(destination.Id);
        }, cancellationToken);

        if (!resolution.IsSuccess || !resolution.DestinationAccountId.HasValue)
        {
            return TransferActionResult.Fail(resolution.Message);
        }

        return await CreateTransferAsync(
            userId,
            new TransferRequest
            {
                SourceAccountId = request.SourceAccountId,
                DestinationInternalAccountId = resolution.DestinationAccountId.Value,
                Amount = request.Amount,
                Memo = request.Memo,
                ScheduledForUtc = request.ScheduledForUtc,
                Frequency = request.Frequency,
                IdempotencyKey = request.IdempotencyKey
            },
            actorRole,
            cancellationToken);
    }

    public async Task<OperationResult> AddFundsAsync(
        Guid userId,
        Guid accountId,
        decimal amount,
        string source,
        string note,
        CancellationToken cancellationToken = default)
    {
        using Activity? activity = _telemetry.ActivitySource.StartActivity(
            "banking.funds.credit",
            ActivityKind.Internal);
        activity?.SetTag("bank.user_id", userId.ToString("N"));
        activity?.SetTag("bank.account_id", accountId.ToString("N"));
        activity?.SetTag("funds.amount", amount);

        if (amount <= 0m)
        {
            _telemetry.RecordFundsMutation("credit", success: false);
            activity?.SetStatus(ActivityStatusCode.Error, "Amount must be greater than 0.");
            return OperationResult.Fail("Amount must be greater than 0.");
        }

        OperationResult result = await _dataStore.WriteAsync(database =>
        {
            RefreshLedgerState(database);
            BankAccount? account = database.Accounts.FirstOrDefault(candidate =>
                candidate.Id == accountId &&
                candidate.UserId == userId);
            if (account is null)
            {
                return OperationResult.Fail("Account not found.");
            }

            if (account.Type == BankAccountType.ExternalLinked)
            {
                return OperationResult.Fail("Linked external accounts cannot be credited directly.");
            }

            if (account.Status == BankAccountStatus.Frozen)
            {
                return OperationResult.Fail("Account is frozen.");
            }

            decimal normalizedAmount = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
            string sourceName = string.IsNullOrWhiteSpace(source) ? "Funds Credit" : source.Trim();
            string description = string.IsNullOrWhiteSpace(note)
                ? $"Credit from {sourceName}"
                : note.Trim();
            string correlationId = GenerateCorrelationId();
            string idempotencyKey = $"credit-{Guid.NewGuid():N}";

            AppendBalancedLedger(
                database,
                GetOrCreateSystemAccount(database, SystemOperatingAccountKey, "Bank Operations Clearing"),
                account,
                normalizedAmount,
                normalizedAmount,
                "funds_credit",
                correlationId,
                idempotencyKey,
                RecordActorType.User,
                userId,
                description);

            database.Transactions.Add(CreateTransaction(
                userId,
                account.Id,
                normalizedAmount,
                "Income",
                sourceName,
                description,
                TransactionState.Posted,
                correlationId,
                idempotencyKey,
                RecordActorType.User,
                userId));

            database.Notifications.Add(new NotificationItem
            {
                UserId = userId,
                Severity = NotificationSeverity.Success,
                Message = $"{normalizedAmount:C} credited to {account.Nickname}."
            });

            AddAudit(
                database,
                userId,
                UserRole.User,
                "add_funds",
                "account",
                account.Id.ToString("N"),
                $"Amount={normalizedAmount};Source={sourceName}");
            RefreshLedgerState(database);

            return OperationResult.Ok("Funds added.");
        }, cancellationToken);

        _telemetry.RecordFundsMutation("credit", result.IsSuccess);
        activity?.SetTag("funds.success", result.IsSuccess);
        if (!result.IsSuccess)
        {
            activity?.SetStatus(ActivityStatusCode.Error, result.Message);
            _logger.LogWarning(
                "Funds credit failed. UserId={UserId} AccountId={AccountId} Amount={Amount} Message={Message}",
                userId,
                accountId,
                amount,
                result.Message);
        }
        else
        {
            _logger.LogInformation(
                "Funds credited. UserId={UserId} AccountId={AccountId} Amount={Amount} Source={Source}",
                userId,
                accountId,
                amount,
                source);
        }

        return result;
    }

    public async Task<OperationResult> SpendFundsAsync(
        Guid userId,
        Guid accountId,
        decimal amount,
        string merchantName,
        string category,
        string note,
        CancellationToken cancellationToken = default)
    {
        using Activity? activity = _telemetry.ActivitySource.StartActivity(
            "banking.funds.debit",
            ActivityKind.Internal);
        activity?.SetTag("bank.user_id", userId.ToString("N"));
        activity?.SetTag("bank.account_id", accountId.ToString("N"));
        activity?.SetTag("funds.amount", amount);

        if (amount <= 0m)
        {
            _telemetry.RecordFundsMutation("debit", success: false);
            activity?.SetStatus(ActivityStatusCode.Error, "Amount must be greater than 0.");
            return OperationResult.Fail("Amount must be greater than 0.");
        }

        OperationResult result = await _dataStore.WriteAsync(database =>
        {
            RefreshLedgerState(database);
            BankAccount? account = database.Accounts.FirstOrDefault(candidate =>
                candidate.Id == accountId &&
                candidate.UserId == userId);
            if (account is null)
            {
                return OperationResult.Fail("Account not found.");
            }

            if (account.Type == BankAccountType.ExternalLinked)
            {
                return OperationResult.Fail("Linked external accounts cannot be debited directly.");
            }

            if (account.Status == BankAccountStatus.Frozen)
            {
                return OperationResult.Fail("Account is frozen.");
            }

            decimal normalizedAmount = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
            if (normalizedAmount > account.AvailableBalance)
            {
                return OperationResult.Fail("Insufficient funds.");
            }

            string merchant = string.IsNullOrWhiteSpace(merchantName) ? "Card Purchase" : merchantName.Trim();
            string spendCategory = string.IsNullOrWhiteSpace(category) ? "General" : category.Trim();
            string description = string.IsNullOrWhiteSpace(note)
                ? $"Purchase at {merchant}"
                : note.Trim();
            string correlationId = GenerateCorrelationId();
            string idempotencyKey = $"debit-{Guid.NewGuid():N}";

            AppendBalancedLedger(
                database,
                account,
                GetOrCreateSystemAccount(database, SystemOperatingAccountKey, "Bank Operations Clearing"),
                normalizedAmount,
                normalizedAmount,
                "funds_debit",
                correlationId,
                idempotencyKey,
                RecordActorType.User,
                userId,
                description);

            database.Transactions.Add(CreateTransaction(
                userId,
                account.Id,
                -normalizedAmount,
                spendCategory,
                merchant,
                description,
                TransactionState.Posted,
                correlationId,
                idempotencyKey,
                RecordActorType.User,
                userId));

            database.Notifications.Add(new NotificationItem
            {
                UserId = userId,
                Severity = NotificationSeverity.Info,
                Message = $"{normalizedAmount:C} spent from {account.Nickname}."
            });

            AddAudit(
                database,
                userId,
                UserRole.User,
                "spend_funds",
                "account",
                account.Id.ToString("N"),
                $"Amount={normalizedAmount};Merchant={merchant};Category={spendCategory}");
            RefreshLedgerState(database);
            CreateLowBalanceAlertIfNeeded(database, account);

            return OperationResult.Ok("Expense posted.");
        }, cancellationToken);

        _telemetry.RecordFundsMutation("debit", result.IsSuccess);
        activity?.SetTag("funds.success", result.IsSuccess);
        if (!result.IsSuccess)
        {
            activity?.SetStatus(ActivityStatusCode.Error, result.Message);
            _logger.LogWarning(
                "Funds debit failed. UserId={UserId} AccountId={AccountId} Amount={Amount} Message={Message}",
                userId,
                accountId,
                amount,
                result.Message);
        }
        else
        {
            _logger.LogInformation(
                "Funds debited. UserId={UserId} AccountId={AccountId} Amount={Amount} Merchant={MerchantName} Category={Category}",
                userId,
                accountId,
                amount,
                merchantName,
                category);
        }

        return result;
    }

    public async Task<OperationResult> CreateBudgetAsync(
        Guid userId,
        string category,
        decimal monthlyLimit,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(category) || monthlyLimit <= 0m)
        {
            return OperationResult.Fail("Category and budget amount are required.");
        }

        return await _dataStore.WriteAsync(database =>
        {
            BudgetRule? existing = database.Budgets.FirstOrDefault(rule =>
                rule.UserId == userId &&
                rule.Category.Equals(category.Trim(), StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                database.Budgets.Add(new BudgetRule
                {
                    UserId = userId,
                    Category = category.Trim(),
                    MonthlyLimit = decimal.Round(monthlyLimit, 2, MidpointRounding.AwayFromZero)
                });
            }
            else
            {
                existing.MonthlyLimit = decimal.Round(monthlyLimit, 2, MidpointRounding.AwayFromZero);
            }

            AddAudit(database, userId, UserRole.User, "save_budget", "budget", category.Trim(), $"Limit={monthlyLimit}");
            return OperationResult.Ok("Budget saved.");
        }, cancellationToken);
    }

    public async Task<OperationResult> CreateGoalAsync(
        Guid userId,
        Guid savingsAccountId,
        string name,
        decimal targetAmount,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name) || targetAmount <= 0m)
        {
            return OperationResult.Fail("Goal name and target are required.");
        }

        return await _dataStore.WriteAsync(database =>
        {
            BankAccount? account = database.Accounts.FirstOrDefault(candidate =>
                candidate.Id == savingsAccountId &&
                candidate.UserId == userId &&
                candidate.Type == BankAccountType.Savings);
            if (account is null)
            {
                return OperationResult.Fail("Savings account not found.");
            }

            database.Goals.Add(new SavingsGoal
            {
                UserId = userId,
                SavingsAccountId = savingsAccountId,
                Name = name.Trim(),
                TargetAmount = decimal.Round(targetAmount, 2, MidpointRounding.AwayFromZero),
                CurrentAmount = 0m
            });

            AddAudit(database, userId, UserRole.User, "create_goal", "goal", name.Trim(), $"Target={targetAmount}");
            return OperationResult.Ok("Goal created.");
        }, cancellationToken);
    }

    public async Task<OperationResult> ContributeGoalAsync(
        Guid userId,
        Guid goalId,
        Guid sourceAccountId,
        decimal amount,
        CancellationToken cancellationToken = default)
    {
        if (amount <= 0m)
        {
            return OperationResult.Fail("Contribution amount must be greater than 0.");
        }

        return await _dataStore.WriteAsync(database =>
        {
            RefreshLedgerState(database);
            SavingsGoal? goal = database.Goals.FirstOrDefault(candidate =>
                candidate.Id == goalId && candidate.UserId == userId);
            if (goal is null)
            {
                return OperationResult.Fail("Goal was not found.");
            }

            BankAccount? source = database.Accounts.FirstOrDefault(candidate =>
                candidate.Id == sourceAccountId && candidate.UserId == userId);
            BankAccount? destination = database.Accounts.FirstOrDefault(candidate =>
                candidate.Id == goal.SavingsAccountId && candidate.UserId == userId);
            if (source is null || destination is null)
            {
                return OperationResult.Fail("Accounts for this goal are unavailable.");
            }

            decimal normalizedAmount = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
            if (normalizedAmount > source.AvailableBalance)
            {
                return OperationResult.Fail("Insufficient funds.");
            }

            if (source.Status == BankAccountStatus.Frozen || destination.Status == BankAccountStatus.Frozen)
            {
                return OperationResult.Fail("One of the involved accounts is frozen.");
            }
            string correlationId = GenerateCorrelationId();
            string idempotencyKey = $"goal-{goalId:N}-{Guid.NewGuid():N}";

            AppendBalancedLedger(
                database,
                source,
                destination,
                normalizedAmount,
                normalizedAmount,
                "goal_contribution",
                correlationId,
                idempotencyKey,
                RecordActorType.User,
                userId,
                $"Contribution to {goal.Name}");

            goal.CurrentAmount = decimal.Round(goal.CurrentAmount + normalizedAmount, 2, MidpointRounding.AwayFromZero);

            database.Transactions.Add(CreateTransaction(
                userId,
                source.Id,
                -normalizedAmount,
                "Goal",
                "Savings Goal",
                $"Contribution to {goal.Name}",
                TransactionState.Posted,
                correlationId,
                idempotencyKey,
                RecordActorType.User,
                userId));
            database.Transactions.Add(CreateTransaction(
                userId,
                destination.Id,
                normalizedAmount,
                "Goal",
                "Savings Goal",
                $"Contribution from {source.Nickname} to {goal.Name}",
                TransactionState.Posted,
                correlationId,
                idempotencyKey,
                RecordActorType.User,
                userId));

            database.Notifications.Add(new NotificationItem
            {
                UserId = userId,
                Severity = NotificationSeverity.Success,
                Message = $"Savings goal contribution posted: {normalizedAmount:C} to {goal.Name}."
            });
            AddAudit(database, userId, UserRole.User, "goal_contribution", "goal", goalId.ToString("N"), $"Amount={normalizedAmount}");
            RefreshLedgerState(database);
            CreateLowBalanceAlertIfNeeded(database, source);
            return OperationResult.Ok("Goal contribution completed.");
        }, cancellationToken);
    }

    public async Task<OperationResult> CreateDisputeAsync(
        Guid userId,
        Guid transactionId,
        string reason,
        string notes,
        CancellationToken cancellationToken = default)
    {
        using Activity? activity = _telemetry.ActivitySource.StartActivity(
            "banking.dispute.create",
            ActivityKind.Internal);
        activity?.SetTag("bank.user_id", userId.ToString("N"));
        activity?.SetTag("bank.transaction_id", transactionId.ToString("N"));

        if (string.IsNullOrWhiteSpace(reason))
        {
            _telemetry.RecordDispute(success: false);
            activity?.SetStatus(ActivityStatusCode.Error, "Reason is required.");
            return OperationResult.Fail("Reason is required.");
        }

        OperationResult result = await _dataStore.WriteAsync(database =>
        {
            BankTransaction? transaction = database.Transactions.FirstOrDefault(candidate =>
                candidate.Id == transactionId && candidate.UserId == userId);
            if (transaction is null)
            {
                return OperationResult.Fail("Transaction does not exist.");
            }

            DisputeTicket dispute = new()
            {
                UserId = userId,
                TransactionId = transactionId,
                Reason = reason.Trim(),
                Notes = (notes ?? string.Empty).Trim(),
                Status = DisputeStatus.Open
            };
            database.Disputes.Add(dispute);

            database.Notifications.Add(new NotificationItem
            {
                UserId = userId,
                Severity = NotificationSeverity.Info,
                Message = $"Dispute ticket #{dispute.Id.ToString("N")[..8]} created."
            });

            AddAudit(database, userId, UserRole.User, "create_dispute", "dispute", dispute.Id.ToString("N"), reason.Trim());
            AddDomainEvent(
                database,
                "DisputeOpened",
                "dispute",
                dispute.Id.ToString("N"),
                userId,
                transaction.CorrelationId,
                reason.Trim());
            return OperationResult.Ok("Dispute ticket created.");
        }, cancellationToken);

        _telemetry.RecordDispute(result.IsSuccess);
        activity?.SetTag("dispute.success", result.IsSuccess);
        if (!result.IsSuccess)
        {
            activity?.SetStatus(ActivityStatusCode.Error, result.Message);
            _logger.LogWarning(
                "Dispute creation failed. UserId={UserId} TransactionId={TransactionId} Message={Message}",
                userId,
                transactionId,
                result.Message);
        }
        else
        {
            _logger.LogInformation(
                "Dispute created. UserId={UserId} TransactionId={TransactionId}",
                userId,
                transactionId);
        }

        return result;
    }

    public async Task<IReadOnlyList<DisputeTicket>> GetDisputesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await _dataStore.ReadAsync(
            database => database.Disputes
                .Where(dispute => dispute.UserId == userId)
                .OrderByDescending(dispute => dispute.CreatedUtc)
                .ToList(),
            cancellationToken);
    }

    public async Task<OperationResult> MarkNotificationReadAsync(
        Guid userId,
        Guid notificationId,
        CancellationToken cancellationToken = default)
    {
        return await _dataStore.WriteAsync(database =>
        {
            NotificationItem? notification = database.Notifications.FirstOrDefault(candidate =>
                candidate.Id == notificationId && candidate.UserId == userId);
            if (notification is null)
            {
                return OperationResult.Fail("Notification not found.");
            }

            notification.IsRead = true;
            return OperationResult.Ok("Notification marked as read.");
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<LoginRecord>> GetLoginHistoryAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await _dataStore.ReadAsync(
            database => database.LoginHistory
                .Where(item => item.UserId == userId)
                .OrderByDescending(item => item.TimestampUtc)
                .Take(40)
                .ToList(),
            cancellationToken);
    }

    public async Task<IReadOnlyList<DeviceRecord>> GetDevicesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        return await _dataStore.ReadAsync(
            database => database.Devices
                .Where(item => item.UserId == userId)
                .OrderByDescending(item => item.LastSeenUtc)
                .ToList(),
            cancellationToken);
    }

    public async Task<StatementExport?> ExportStatementCsvAsync(
        Guid userId,
        Guid accountId,
        int year,
        int month,
        CancellationToken cancellationToken = default)
    {
        return await _dataStore.ReadAsync(database =>
        {
            RefreshLedgerState(database);
            BankAccount? account = database.Accounts.FirstOrDefault(candidate =>
                candidate.Id == accountId && candidate.UserId == userId);
            if (account is null)
            {
                return null;
            }

            List<BankTransaction> monthTransactions = FilterStatementTransactions(database, userId, accountId, year, month);
            string csv = BuildCsv(monthTransactions);
            return new StatementExport(
                $"statement-{year:D4}-{month:D2}-{MaskAccountNumber(account.AccountNumber)}.csv",
                "text/csv",
                Encoding.UTF8.GetBytes(csv));
        }, cancellationToken);
    }

    public async Task<StatementExport?> ExportStatementPdfAsync(
        Guid userId,
        Guid accountId,
        int year,
        int month,
        CancellationToken cancellationToken = default)
    {
        return await _dataStore.ReadAsync(database =>
        {
            RefreshLedgerState(database);
            BankAccount? account = database.Accounts.FirstOrDefault(candidate =>
                candidate.Id == accountId && candidate.UserId == userId);
            if (account is null)
            {
                return null;
            }

            List<BankTransaction> monthTransactions = FilterStatementTransactions(database, userId, accountId, year, month);
            string text = BuildPseudoPdfText(account, monthTransactions, year, month);
            return new StatementExport(
                $"statement-{year:D4}-{month:D2}-{MaskAccountNumber(account.AccountNumber)}.pdf",
                "application/pdf",
                Encoding.UTF8.GetBytes(text));
        }, cancellationToken);
    }

    public async Task<AdminSnapshot> GetAdminSnapshotAsync(CancellationToken cancellationToken = default)
    {
        return await _dataStore.ReadAsync(database =>
        {
            RefreshLedgerState(database);
            List<AdminUserView> users = database.Users
                .OrderBy(user => user.CreatedUtc)
                .Select(user =>
                {
                    List<BankAccount> accounts = database.Accounts
                        .Where(account => account.UserId == user.Id && !account.IsSystemAccount)
                        .ToList();
                    return new AdminUserView(
                        user.Id,
                        user.Email,
                        user.FirstName,
                        user.LastName,
                        user.Role,
                        accounts.Count,
                        accounts.Sum(account => account.AvailableBalance));
                })
                .ToList();

            List<AdminAccountView> accounts = database.Accounts
                .Where(account => !account.IsSystemAccount)
                .OrderBy(account => account.UserId)
                .ThenBy(account => account.Type)
                .Select(account => new AdminAccountView(
                    account.Id,
                    account.UserId,
                    account.Nickname,
                    account.Type,
                    account.Status,
                    account.AvailableBalance,
                    MaskAccountNumber(account.AccountNumber),
                    account.RoutingNumber))
                .ToList();

            List<AdminFraudTransferView> fraudTransfers = database.Transfers
                .Where(transfer =>
                    transfer.State == TransferState.UnderReview ||
                    transfer.FraudScore > 0)
                .OrderByDescending(transfer => transfer.RequestedUtc)
                .Take(150)
                .Select(transfer =>
                {
                    BankAccount? source = database.Accounts.FirstOrDefault(account => account.Id == transfer.SourceAccountId);
                    return new AdminFraudTransferView(
                        transfer.Id,
                        transfer.UserId,
                        source?.Nickname ?? "Unknown Account",
                        transfer.Amount,
                        transfer.IsExternalAch,
                        transfer.State,
                        transfer.FraudScore,
                        transfer.FraudFlags,
                        transfer.RequestedUtc);
                })
                .ToList();

            List<DisputeTicket> disputes = database.Disputes
                .OrderByDescending(dispute => dispute.CreatedUtc)
                .ToList();
            List<AuditLogEntry> audit = database.AuditLogs
                .OrderByDescending(entry => entry.TimestampUtc)
                .Take(300)
                .ToList();
            List<AdminDomainEventView> domainEvents = database.DomainEvents
                .OrderByDescending(entry => entry.CreatedUtc)
                .Take(200)
                .Select(entry => new AdminDomainEventView(
                    entry.CreatedUtc,
                    entry.EventType,
                    entry.EntityType,
                    entry.EntityId,
                    entry.UserId,
                    entry.Metadata))
                .ToList();

            return new AdminSnapshot(users, accounts, fraudTransfers, disputes, audit, domainEvents);
        }, cancellationToken);
    }

    public async Task<OperationResult> AdminManualAdjustmentAsync(
        Guid adminUserId,
        Guid accountId,
        decimal amount,
        string note,
        CancellationToken cancellationToken = default)
    {
        if (amount == 0m)
        {
            return OperationResult.Fail("Amount cannot be zero.");
        }

        return await _dataStore.WriteAsync(database =>
        {
            RefreshLedgerState(database);
            BankUser? admin = database.Users.FirstOrDefault(user => user.Id == adminUserId);
            if (admin is null || admin.Role != UserRole.Admin)
            {
                return OperationResult.Fail("Admin privileges required.");
            }

            BankAccount? account = database.Accounts.FirstOrDefault(candidate => candidate.Id == accountId);
            if (account is null)
            {
                return OperationResult.Fail("Account not found.");
            }

            decimal normalizedAmount = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
            string description = string.IsNullOrWhiteSpace(note) ? "Manual adjustment" : note.Trim();
            string correlationId = GenerateCorrelationId();
            string idempotencyKey = $"admin-adjust-{Guid.NewGuid():N}";

            if (normalizedAmount > 0m)
            {
                AppendBalancedLedger(
                    database,
                    GetOrCreateSystemAccount(database, SystemOperatingAccountKey, "Bank Operations Clearing"),
                    account,
                    normalizedAmount,
                    normalizedAmount,
                    "manual_adjustment_credit",
                    correlationId,
                    idempotencyKey,
                    RecordActorType.Admin,
                    adminUserId,
                    description);
            }
            else
            {
                decimal absolute = Math.Abs(normalizedAmount);
                AppendBalancedLedger(
                    database,
                    account,
                    GetOrCreateSystemAccount(database, SystemOperatingAccountKey, "Bank Operations Clearing"),
                    absolute,
                    absolute,
                    "manual_adjustment_debit",
                    correlationId,
                    idempotencyKey,
                    RecordActorType.Admin,
                    adminUserId,
                    description);
            }

            database.Transactions.Add(CreateTransaction(
                account.UserId,
                account.Id,
                normalizedAmount,
                "Adjustment",
                "Bank Operations",
                description,
                TransactionState.Posted,
                correlationId,
                idempotencyKey,
                RecordActorType.Admin,
                adminUserId));

            AddAudit(database, adminUserId, UserRole.Admin, "manual_adjustment", "account", accountId.ToString("N"), $"Amount={normalizedAmount};Note={note}");
            AddDomainEvent(
                database,
                "ManualAdjustmentPosted",
                "account",
                accountId.ToString("N"),
                account.UserId,
                correlationId,
                $"Amount={normalizedAmount}");
            database.Notifications.Add(new NotificationItem
            {
                UserId = account.UserId,
                Severity = NotificationSeverity.Info,
                Message = $"Bank operations posted a manual adjustment of {normalizedAmount:C}."
            });

            RefreshLedgerState(database);
            QueueAccountBalance(database, account, "Manual adjustment posted.");

            return OperationResult.Ok("Manual adjustment posted.");
        }, cancellationToken);
    }

    public async Task<OperationResult> AdminReviewDisputeAsync(
        Guid adminUserId,
        Guid disputeId,
        DisputeStatus status,
        string adminNotes,
        CancellationToken cancellationToken = default)
    {
        using Activity? activity = _telemetry.ActivitySource.StartActivity(
            "banking.dispute.review",
            ActivityKind.Internal);
        activity?.SetTag("bank.admin_user_id", adminUserId.ToString("N"));
        activity?.SetTag("bank.dispute_id", disputeId.ToString("N"));
        activity?.SetTag("dispute.status", status.ToString());

        OperationResult result = await _dataStore.WriteAsync(database =>
        {
            BankUser? admin = database.Users.FirstOrDefault(user => user.Id == adminUserId);
            if (admin is null || admin.Role != UserRole.Admin)
            {
                return OperationResult.Fail("Admin privileges required.");
            }

            DisputeTicket? dispute = database.Disputes.FirstOrDefault(candidate => candidate.Id == disputeId);
            if (dispute is null)
            {
                return OperationResult.Fail("Dispute not found.");
            }

            dispute.Status = status;
            dispute.AdminNotes = (adminNotes ?? string.Empty).Trim();

            database.Notifications.Add(new NotificationItem
            {
                UserId = dispute.UserId,
                Severity = NotificationSeverity.Info,
                Message = $"Dispute #{dispute.Id.ToString("N")[..8]} moved to {status}."
            });

            AddAudit(database, adminUserId, UserRole.Admin, "review_dispute", "dispute", dispute.Id.ToString("N"), $"Status={status}");
            AddDomainEvent(
                database,
                "DisputeStatusChanged",
                "dispute",
                dispute.Id.ToString("N"),
                dispute.UserId,
                dispute.Id.ToString("N"),
                $"Status={status}");
            return OperationResult.Ok("Dispute updated.");
        }, cancellationToken);

        if (!result.IsSuccess)
        {
            activity?.SetStatus(ActivityStatusCode.Error, result.Message);
            _logger.LogWarning(
                "Dispute review failed. AdminUserId={AdminUserId} DisputeId={DisputeId} Status={Status} Message={Message}",
                adminUserId,
                disputeId,
                status,
                result.Message);
        }
        else
        {
            _logger.LogInformation(
                "Dispute reviewed. AdminUserId={AdminUserId} DisputeId={DisputeId} Status={Status}",
                adminUserId,
                disputeId,
                status);
        }

        return result;
    }

    public async Task<OperationResult> AdminReviewTransferAsync(
        Guid adminUserId,
        Guid transferId,
        bool approve,
        string adminNotes,
        CancellationToken cancellationToken = default)
    {
        return await _dataStore.WriteAsync(database =>
        {
            RefreshLedgerState(database);
            BankUser? admin = database.Users.FirstOrDefault(user => user.Id == adminUserId);
            if (admin is null || admin.Role != UserRole.Admin)
            {
                return OperationResult.Fail("Admin privileges required.");
            }

            BankTransfer? transfer = database.Transfers.FirstOrDefault(candidate => candidate.Id == transferId);
            if (transfer is null)
            {
                return OperationResult.Fail("Transfer not found.");
            }

            if (transfer.State != TransferState.UnderReview)
            {
                return OperationResult.Fail("Transfer is not awaiting review.");
            }

            transfer.ReviewedUtc = DateTimeOffset.UtcNow;
            transfer.ReviewedByUserId = adminUserId;
            transfer.ReviewNotes = (adminNotes ?? string.Empty).Trim();

            if (!approve)
            {
                transfer.State = TransferState.Cancelled;
                database.Notifications.Add(new NotificationItem
                {
                    UserId = transfer.UserId,
                    Severity = NotificationSeverity.Warning,
                    Message = $"Transfer {transfer.Id.ToString("N")[..8]} was rejected during fraud review."
                });
                AddAudit(
                    database,
                    adminUserId,
                    UserRole.Admin,
                    "reject_reviewed_transfer",
                    "transfer",
                    transfer.Id.ToString("N"),
                    transfer.FraudFlags);
                AddDomainEvent(
                    database,
                    "TransferRejected",
                    "transfer",
                    transfer.Id.ToString("N"),
                    transfer.UserId,
                    transfer.CorrelationId,
                    transfer.FraudFlags);
                QueueTransferStatus(transfer, "Transfer rejected during fraud review.", isWarning: true);
                _realtimeQueue.EnqueueAdmins("fraud.alert", new
                {
                    transferId = transfer.Id.ToString("N"),
                    userId = transfer.UserId.ToString("N"),
                    amount = transfer.Amount,
                    fraudScore = transfer.FraudScore,
                    fraudFlags = transfer.FraudFlags,
                    message = $"Transfer {transfer.Id.ToString("N")[..8]} was rejected."
                });
                return OperationResult.Ok("Transfer rejected.");
            }

            return ApproveTransferFromReview(database, transfer, adminUserId);
        }, cancellationToken);
    }

    public async Task<OperationResult> AdminFreezeAccountAsync(
        Guid adminUserId,
        Guid accountId,
        bool freeze,
        CancellationToken cancellationToken = default)
    {
        return await ToggleFreezeAccountAsync(adminUserId, UserRole.Admin, accountId, freeze, cancellationToken);
    }

    public bool TryValidateAccessToken(string token, out TokenPayload payload)
    {
        return _securityService.TryValidateAccessToken(token, DateTimeOffset.UtcNow, out payload);
    }

    private void CleanupExpiredRows(BankDatabase database)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        RefreshLedgerState(database);

        foreach (BankUser user in database.Users)
        {
            user.RefreshSessions.RemoveAll(session => session.ExpiresUtc <= now);
        }

        database.IdempotencyKeys.RemoveAll(record => record.CreatedUtc <= now.AddHours(-24));
    }

    private LoginCompleteResult CompleteLoginSuccess(
        BankDatabase database,
        BankUser user,
        DateTimeOffset now,
        string deviceId,
        string deviceName,
        string ipAddress,
        string userAgent)
    {
        user.LastLoginUtc = now;
        user.FailedLoginAttempts = 0;
        user.LockedUntilUtc = null;

        DeviceRecord? existingDevice = database.Devices.FirstOrDefault(candidate =>
            candidate.UserId == user.Id && candidate.DeviceId == deviceId);
        bool isNewDevice = existingDevice is null;
        if (existingDevice is null)
        {
            existingDevice = new DeviceRecord
            {
                UserId = user.Id,
                DeviceId = deviceId,
                DeviceName = string.IsNullOrWhiteSpace(deviceName) ? "Unknown Device" : deviceName,
                Trusted = true
            };
            database.Devices.Add(existingDevice);
        }
        else
        {
            existingDevice.LastSeenUtc = now;
            existingDevice.DeviceName = string.IsNullOrWhiteSpace(deviceName)
                ? existingDevice.DeviceName
                : deviceName;
        }

        database.LoginHistory.Add(new LoginRecord
        {
            UserId = user.Id,
            DeviceId = deviceId,
            DeviceName = existingDevice.DeviceName,
            IpAddress = ipAddress,
            UserAgent = userAgent,
            IsNewDevice = isNewDevice
        });

        if (isNewDevice && user.Settings.SecurityAlertsEnabled)
        {
            database.Notifications.Add(new NotificationItem
            {
                UserId = user.Id,
                Severity = NotificationSeverity.Warning,
                Message = $"New device login detected: {existingDevice.DeviceName} ({ipAddress})"
            });
        }

        string accessToken = _securityService.IssueAccessToken(
            user.Id,
            user.Role,
            now,
            BankingSecurityService.AccessTokenLifetime);
        string refreshToken = _securityService.GenerateRefreshToken();
        user.RefreshSessions.Add(new RefreshSession
        {
            RefreshToken = refreshToken,
            IssuedUtc = now,
            ExpiresUtc = now.Add(BankingSecurityService.RefreshTokenLifetime),
            DeviceId = deviceId
        });

        AddAudit(
            database,
            user.Id,
            user.Role,
            "login_success",
            "user",
            user.Id.ToString("N"),
            $"Device={existingDevice.DeviceName},Ip={ipAddress}");

        return LoginCompleteResult.Ok(user.Id, user.Role, user.FirstName, accessToken, refreshToken);
    }

    private static BankAccount CreateAccount(Guid userId, BankAccountType type, string nickname)
    {
        string accountNumber = RandomNumberGenerator.GetInt32(100_000_000, 999_999_999).ToString("D9");
        return new BankAccount
        {
            UserId = userId,
            Type = type,
            Nickname = nickname,
            AccountNumber = accountNumber,
            RoutingNumber = CreateRoutingNumber(),
            AvailableBalance = 0m,
            LedgerBalance = 0m
        };
    }

    private static string CreateRoutingNumber()
    {
        return RandomNumberGenerator.GetInt32(100_000_000, 999_999_999).ToString("D9");
    }

    private static string GenerateCorrelationId() => Guid.NewGuid().ToString("N");

    private static RecordActorType ToRecordActor(UserRole role)
    {
        return role switch
        {
            UserRole.Admin => RecordActorType.Admin,
            UserRole.User => RecordActorType.User,
            _ => RecordActorType.System
        };
    }

    private static BankAccount GetOrCreateSystemAccount(BankDatabase database, string internalKey, string nickname)
    {
        BankAccount? existing = database.Accounts.FirstOrDefault(account =>
            account.IsSystemAccount &&
            string.Equals(account.InternalKey, internalKey, StringComparison.Ordinal));

        if (existing is not null)
        {
            return existing;
        }

        BankAccount created = new()
        {
            UserId = SystemUserId,
            Type = BankAccountType.Checking,
            Nickname = nickname,
            AccountNumber = RandomNumberGenerator.GetInt32(100_000_000, 999_999_999).ToString("D9"),
            RoutingNumber = CreateRoutingNumber(),
            IsSystemAccount = true,
            InternalKey = internalKey
        };

        database.Accounts.Add(created);
        return created;
    }

    private static void AppendOpeningBalanceEntries(
        BankDatabase database,
        BankAccount destination,
        decimal amount,
        string correlationId,
        string idempotencyKey)
    {
        if (amount <= 0m)
        {
            return;
        }

        AppendBalancedLedger(
            database,
            GetOrCreateSystemAccount(database, SystemSeedAccountKey, "Seed Capital"),
            destination,
            amount,
            amount,
            "opening_balance",
            correlationId,
            idempotencyKey,
            RecordActorType.System,
            null,
            $"Opening balance for {destination.Nickname}");
    }

    private static void AppendBalancedLedger(
        BankDatabase database,
        BankAccount source,
        BankAccount destination,
        decimal availableAmount,
        decimal ledgerAmount,
        string entryType,
        string correlationId,
        string idempotencyKey,
        RecordActorType createdBy,
        Guid? createdByUserId,
        string description,
        Guid? transferId = null)
    {
        decimal normalizedAvailable = decimal.Round(Math.Abs(availableAmount), 2, MidpointRounding.AwayFromZero);
        decimal normalizedLedger = decimal.Round(Math.Abs(ledgerAmount), 2, MidpointRounding.AwayFromZero);

        database.LedgerEntries.Add(new LedgerEntry
        {
            AccountId = source.Id,
            UserId = source.UserId,
            TransferId = transferId,
            AvailableDelta = -normalizedAvailable,
            LedgerDelta = -normalizedLedger,
            EntryType = entryType,
            Description = description,
            CorrelationId = correlationId,
            IdempotencyKey = idempotencyKey,
            CreatedBy = createdBy,
            CreatedByUserId = createdByUserId
        });

        database.LedgerEntries.Add(new LedgerEntry
        {
            AccountId = destination.Id,
            UserId = destination.UserId,
            TransferId = transferId,
            AvailableDelta = normalizedAvailable,
            LedgerDelta = normalizedLedger,
            EntryType = entryType,
            Description = description,
            CorrelationId = correlationId,
            IdempotencyKey = idempotencyKey,
            CreatedBy = createdBy,
            CreatedByUserId = createdByUserId
        });
    }

    private static void RefreshLedgerState(BankDatabase database)
    {
        GetOrCreateSystemAccount(database, SystemSeedAccountKey, "Seed Capital");
        GetOrCreateSystemAccount(database, SystemOperatingAccountKey, "Bank Operations Clearing");
        GetOrCreateSystemAccount(database, SystemSettlementAccountKey, "ACH Settlement Clearing");

        foreach (BankTransfer transfer in database.Transfers)
        {
            if (string.IsNullOrWhiteSpace(transfer.CorrelationId))
            {
                transfer.CorrelationId = GenerateCorrelationId();
            }
        }

        foreach (BankTransaction transaction in database.Transactions)
        {
            if (string.IsNullOrWhiteSpace(transaction.CorrelationId))
            {
                transaction.CorrelationId = GenerateCorrelationId();
            }

            if (string.IsNullOrWhiteSpace(transaction.IdempotencyKey))
            {
                transaction.IdempotencyKey = $"tx-{transaction.Id:N}";
            }
        }

        List<BankAccount> bootstrapAccounts = database.Accounts
            .Where(account =>
                !account.IsSystemAccount &&
                !database.LedgerEntries.Any(entry => entry.AccountId == account.Id) &&
                (account.AvailableBalance != 0m || account.LedgerBalance != 0m))
            .ToList();

        if (bootstrapAccounts.Count > 0)
        {
            foreach (BankAccount account in bootstrapAccounts)
            {
                AppendBalancedLedger(
                    database,
                    GetOrCreateSystemAccount(database, SystemSeedAccountKey, "Seed Capital"),
                    account,
                    account.AvailableBalance,
                    account.LedgerBalance,
                    "legacy_balance_bootstrap",
                    GenerateCorrelationId(),
                    $"bootstrap-{account.Id:N}",
                    RecordActorType.System,
                    null,
                    "Legacy balance bootstrap");
            }
        }

        Dictionary<Guid, (decimal Available, decimal Ledger)> totals = database.LedgerEntries
            .GroupBy(entry => entry.AccountId)
            .ToDictionary(
                group => group.Key,
                group => (
                    Available: decimal.Round(group.Sum(entry => entry.AvailableDelta), 2, MidpointRounding.AwayFromZero),
                    Ledger: decimal.Round(group.Sum(entry => entry.LedgerDelta), 2, MidpointRounding.AwayFromZero)));

        foreach (BankAccount account in database.Accounts)
        {
            if (totals.TryGetValue(account.Id, out (decimal Available, decimal Ledger) balance))
            {
                account.AvailableBalance = balance.Available;
                account.LedgerBalance = balance.Ledger;
            }
            else
            {
                account.AvailableBalance = 0m;
                account.LedgerBalance = 0m;
            }
        }
    }

    private static BankTransaction CreateTransaction(
        Guid userId,
        Guid accountId,
        decimal amount,
        string category,
        string merchant,
        string description,
        TransactionState state,
        string correlationId = "",
        string idempotencyKey = "",
        RecordActorType createdBy = RecordActorType.System,
        Guid? createdByUserId = null,
        Guid? transferId = null)
    {
        return new BankTransaction
        {
            UserId = userId,
            AccountId = accountId,
            Amount = decimal.Round(amount, 2, MidpointRounding.AwayFromZero),
            Category = string.IsNullOrWhiteSpace(category) ? "General" : category,
            MerchantName = string.IsNullOrWhiteSpace(merchant) ? "Unknown Merchant" : merchant,
            Description = string.IsNullOrWhiteSpace(description) ? "Transaction" : description,
            State = state,
            PostedUtc = state == TransactionState.Posted ? DateTimeOffset.UtcNow : null,
            ReferenceCode = $"TX-{Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()}",
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? GenerateCorrelationId() : correlationId,
            IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? Guid.NewGuid().ToString("N") : idempotencyKey,
            CreatedBy = createdBy,
            CreatedByUserId = createdByUserId,
            TransferId = transferId
        };
    }

    private static bool LooksLikeEmail(string email)
    {
        return email.Contains('@') && email.Contains('.');
    }

    private static string BuildDisplayName(BankUser user)
    {
        string full = $"{user.FirstName} {user.LastName}".Trim();
        return string.IsNullOrWhiteSpace(full) ? user.Email : full;
    }

    private static int NormalizeDays(int requestedDays)
    {
        return requestedDays switch
        {
            7 => 7,
            30 => 30,
            90 => 90,
            365 => 365,
            _ => 30
        };
    }

    private static string BuildTransferDescription(BankTransfer transfer, BankDatabase database)
    {
        string transferShortId = transfer.Id.ToString("N")[..6].ToUpperInvariant();
        if (transfer.IsExternalAch && transfer.DestinationExternalAccountId.HasValue)
        {
            LinkedExternalAccount? external = database.LinkedExternalAccounts.FirstOrDefault(account =>
                account.Id == transfer.DestinationExternalAccountId.Value);
            string target = external is null ? "External Account" : $"{external.BankName} {external.AccountMask}";
            return $"ACH transfer to {target} ({transferShortId})";
        }

        if (transfer.DestinationInternalAccountId.HasValue)
        {
            BankAccount? destination = database.Accounts.FirstOrDefault(account =>
                account.Id == transfer.DestinationInternalAccountId.Value);
            string target = destination is null ? "Internal Account" : destination.Nickname;
            return $"Transfer to {target} ({transferShortId})";
        }

        return $"Transfer ({transferShortId})";
    }

    private static void CreateLowBalanceAlertIfNeeded(BankDatabase database, BankAccount account)
    {
        if (!account.IsSystemAccount &&
            account.Type == BankAccountType.Checking &&
            account.AvailableBalance < 100m)
        {
            database.Notifications.Add(new NotificationItem
            {
                UserId = account.UserId,
                Severity = NotificationSeverity.Warning,
                Message = $"Low balance alert on {account.Nickname}: {account.AvailableBalance:C}"
            });
        }
    }

    private static string MaskAccountNumber(string accountNumber)
    {
        if (string.IsNullOrWhiteSpace(accountNumber))
        {
            return "****";
        }

        string last4 = accountNumber.Length <= 4 ? accountNumber : accountNumber[^4..];
        return $"****{last4}";
    }

    private void AddAudit(
        BankDatabase database,
        Guid? actorUserId,
        UserRole actorRole,
        string action,
        string entityType,
        string entityId,
        string metadata)
    {
        database.AuditLogs.Add(new AuditLogEntry
        {
            ActorUserId = actorUserId,
            ActorRole = actorRole,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Metadata = metadata
        });

        _realtimeQueue.EnqueueAdmins("audit.feed", new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            actorUserId = actorUserId?.ToString("N"),
            actorRole = actorRole.ToString(),
            action,
            entityType,
            entityId,
            metadata
        });
    }

    private static void AddDomainEvent(
        BankDatabase database,
        string eventType,
        string entityType,
        string entityId,
        Guid? userId,
        string correlationId,
        string metadata)
    {
        database.DomainEvents.Add(new DomainEventRecord
        {
            EventType = eventType,
            EntityType = entityType,
            EntityId = entityId,
            UserId = userId,
            CorrelationId = correlationId,
            Metadata = metadata
        });

        const int maxEvents = 1000;
        if (database.DomainEvents.Count > maxEvents)
        {
            int overflow = database.DomainEvents.Count - maxEvents;
            database.DomainEvents.RemoveRange(0, overflow);
        }
    }

    private static FraudAssessment AssessTransferRisk(
        BankDatabase database,
        BankUser user,
        BankAccount source,
        decimal amount,
        bool isExternal,
        Guid? destinationExternalAccountId,
        DateTimeOffset now)
    {
        int score = 0;
        List<string> reasons = new();

        int recentTransfers = database.Transfers.Count(transfer =>
            transfer.UserId == user.Id &&
            transfer.RequestedUtc >= now.AddMinutes(-10) &&
            transfer.State != TransferState.Cancelled);
        if (recentTransfers >= 3)
        {
            score += 35;
            reasons.Add($"velocity:{recentTransfers}/10m");
        }

        decimal threshold = isExternal ? 750m : 1500m;
        if (amount >= threshold)
        {
            score += isExternal ? 35 : 25;
            reasons.Add($"amount:{amount.ToString("0.00", CultureInfo.InvariantCulture)}");
        }

        if (source.AvailableBalance > 0m && amount >= decimal.Round(source.AvailableBalance * 0.80m, 2, MidpointRounding.AwayFromZero))
        {
            score += 20;
            reasons.Add("balance-drain");
        }

        List<LoginRecord> recentLogins = database.LoginHistory
            .Where(entry => entry.UserId == user.Id)
            .OrderByDescending(entry => entry.TimestampUtc)
            .Take(2)
            .ToList();

        LoginRecord? latestLogin = recentLogins.FirstOrDefault();
        if (latestLogin is not null &&
            latestLogin.IsNewDevice &&
            latestLogin.TimestampUtc >= now.AddHours(-24))
        {
            score += 25;
            reasons.Add("new-device");
        }

        if (recentLogins.Count >= 2 &&
            recentLogins[0].TimestampUtc >= now.AddHours(-24) &&
            !string.Equals(recentLogins[0].IpAddress, recentLogins[1].IpAddress, StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
            reasons.Add("new-ip");
        }

        if (isExternal && destinationExternalAccountId.HasValue)
        {
            LinkedExternalAccount? linkedAccount = database.LinkedExternalAccounts.FirstOrDefault(account =>
                account.Id == destinationExternalAccountId.Value &&
                account.UserId == user.Id);
            if (linkedAccount is not null && linkedAccount.LinkedUtc >= now.AddHours(-24))
            {
                score += 15;
                reasons.Add("fresh-external-link");
            }
        }

        score = Math.Min(score, 100);
        return score >= 50
            ? new FraudAssessment(true, score, string.Join(", ", reasons))
            : new FraudAssessment(false, score, string.Join(", ", reasons));
    }

    private void QueueTransferStatus(BankTransfer transfer, string message, bool isWarning = false)
    {
        _realtimeQueue.EnqueueUser(transfer.UserId, "transfer.status", new
        {
            transferId = transfer.Id.ToString("N"),
            state = transfer.State.ToString(),
            amount = transfer.Amount,
            isExternalAch = transfer.IsExternalAch,
            fraudScore = transfer.FraudScore,
            fraudFlags = transfer.FraudFlags,
            message,
            isWarning
        });
    }

    private void QueueAccountBalance(BankDatabase database, BankAccount account, string message)
    {
        if (account.IsSystemAccount)
        {
            return;
        }

        decimal totalBalance = database.Accounts
            .Where(candidate => candidate.UserId == account.UserId && !candidate.IsSystemAccount)
            .Sum(candidate => candidate.AvailableBalance);

        _realtimeQueue.EnqueueUser(account.UserId, "account.balance", new
        {
            accountId = account.Id.ToString("N"),
            availableBalance = account.AvailableBalance,
            ledgerBalance = account.LedgerBalance,
            totalBalance,
            message
        });
    }

    private OperationResult ApproveTransferFromReview(
        BankDatabase database,
        BankTransfer transfer,
        Guid adminUserId)
    {
        RefreshLedgerState(database);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        BankAccount? source = database.Accounts.FirstOrDefault(account =>
            account.Id == transfer.SourceAccountId &&
            account.UserId == transfer.UserId);
        if (source is null || source.Status == BankAccountStatus.Frozen)
        {
            transfer.State = TransferState.Cancelled;
            AddAudit(database, adminUserId, UserRole.Admin, "approve_reviewed_transfer_failed", "transfer", transfer.Id.ToString("N"), "Source unavailable.");
            AddDomainEvent(database, "TransferApprovalFailed", "transfer", transfer.Id.ToString("N"), transfer.UserId, transfer.CorrelationId, "Source unavailable");
            return OperationResult.Fail("Source account is unavailable.");
        }

        if (transfer.Amount > source.AvailableBalance)
        {
            transfer.State = TransferState.Cancelled;
            AddAudit(database, adminUserId, UserRole.Admin, "approve_reviewed_transfer_failed", "transfer", transfer.Id.ToString("N"), "Insufficient funds.");
            AddDomainEvent(database, "TransferApprovalFailed", "transfer", transfer.Id.ToString("N"), transfer.UserId, transfer.CorrelationId, "Insufficient funds");
            return OperationResult.Fail("Insufficient available funds.");
        }

        bool returnToSchedule = transfer.NextRunUtc.HasValue &&
            (transfer.NextRunUtc.Value > now || transfer.Frequency != TransferFrequency.OneTime);
        if (returnToSchedule)
        {
            transfer.State = TransferState.Scheduled;
            database.Notifications.Add(new NotificationItem
            {
                UserId = transfer.UserId,
                Severity = NotificationSeverity.Info,
                Message = $"Transfer {transfer.Id.ToString("N")[..8]} was approved and returned to schedule."
            });
            AddAudit(database, adminUserId, UserRole.Admin, "approve_reviewed_transfer", "transfer", transfer.Id.ToString("N"), "Returned to schedule");
            AddDomainEvent(database, "TransferApproved", "transfer", transfer.Id.ToString("N"), transfer.UserId, transfer.CorrelationId, "State=Scheduled");
            QueueTransferStatus(transfer, "Transfer approved and returned to schedule.");
            return OperationResult.Ok("Transfer approved and returned to schedule.");
        }

        string description = BuildTransferDescription(transfer, database);
        if (transfer.IsExternalAch)
        {
            transfer.State = TransferState.Pending;
            transfer.SettlesUtc = now.AddMinutes(2);

            AppendBalancedLedger(
                database,
                source,
                GetOrCreateSystemAccount(database, SystemSettlementAccountKey, "ACH Settlement Clearing"),
                transfer.Amount,
                0m,
                "external_transfer_hold",
                transfer.CorrelationId,
                transfer.IdempotencyKey,
                RecordActorType.Admin,
                adminUserId,
                description,
                transfer.Id);

            database.Transactions.Add(CreateTransaction(
                transfer.UserId,
                source.Id,
                -transfer.Amount,
                "Transfer",
                "ACH External",
                description,
                TransactionState.Pending,
                transfer.CorrelationId,
                transfer.IdempotencyKey,
                RecordActorType.Admin,
                adminUserId,
                transfer.Id));

            database.Notifications.Add(new NotificationItem
            {
                UserId = transfer.UserId,
                Severity = NotificationSeverity.Warning,
                Message = $"Transfer {transfer.Id.ToString("N")[..8]} was approved and is pending settlement."
            });

            AddAudit(database, adminUserId, UserRole.Admin, "approve_reviewed_transfer", "transfer", transfer.Id.ToString("N"), "State=Pending");
            AddDomainEvent(database, "TransferApproved", "transfer", transfer.Id.ToString("N"), transfer.UserId, transfer.CorrelationId, "State=Pending");
            RefreshLedgerState(database);
            CreateLowBalanceAlertIfNeeded(database, source);
            QueueTransferStatus(transfer, "Transfer approved and pending settlement.");
            QueueAccountBalance(database, source, "Available balance updated for ACH hold.");
            return OperationResult.Ok("Transfer approved and pending settlement.");
        }

        if (!transfer.DestinationInternalAccountId.HasValue)
        {
            transfer.State = TransferState.Cancelled;
            AddAudit(database, adminUserId, UserRole.Admin, "approve_reviewed_transfer_failed", "transfer", transfer.Id.ToString("N"), "Missing destination.");
            AddDomainEvent(database, "TransferApprovalFailed", "transfer", transfer.Id.ToString("N"), transfer.UserId, transfer.CorrelationId, "Missing destination");
            return OperationResult.Fail("Destination account is required.");
        }

        BankAccount? destination = database.Accounts.FirstOrDefault(account =>
            account.Id == transfer.DestinationInternalAccountId.Value &&
            !account.IsSystemAccount &&
            account.Type != BankAccountType.ExternalLinked);
        if (destination is null || destination.Status == BankAccountStatus.Frozen)
        {
            transfer.State = TransferState.Cancelled;
            AddAudit(database, adminUserId, UserRole.Admin, "approve_reviewed_transfer_failed", "transfer", transfer.Id.ToString("N"), "Destination unavailable.");
            AddDomainEvent(database, "TransferApprovalFailed", "transfer", transfer.Id.ToString("N"), transfer.UserId, transfer.CorrelationId, "Destination unavailable");
            return OperationResult.Fail("Destination account is unavailable.");
        }

        if (destination.Id == source.Id)
        {
            transfer.State = TransferState.Cancelled;
            AddAudit(database, adminUserId, UserRole.Admin, "approve_reviewed_transfer_failed", "transfer", transfer.Id.ToString("N"), "Source and destination match.");
            AddDomainEvent(database, "TransferApprovalFailed", "transfer", transfer.Id.ToString("N"), transfer.UserId, transfer.CorrelationId, "Source and destination match");
            return OperationResult.Fail("Source and destination must be different.");
        }

        bool isPeerTransfer = destination.UserId != transfer.UserId;
        BankUser? sender = database.Users.FirstOrDefault(user => user.Id == transfer.UserId);
        string senderDisplayName = sender is null ? "another user" : BuildDisplayName(sender);

        transfer.State = TransferState.Posted;
        transfer.SettlesUtc = now;

        AppendBalancedLedger(
            database,
            source,
            destination,
            transfer.Amount,
            transfer.Amount,
            "internal_transfer_posted",
            transfer.CorrelationId,
            transfer.IdempotencyKey,
            RecordActorType.Admin,
            adminUserId,
            description,
            transfer.Id);

        database.Transactions.Add(CreateTransaction(
            transfer.UserId,
            source.Id,
            -transfer.Amount,
            "Transfer",
            isPeerTransfer ? "P2P Transfer" : "Internal Transfer",
            isPeerTransfer ? $"Transfer to {destination.Nickname}" : description,
            TransactionState.Posted,
            transfer.CorrelationId,
            transfer.IdempotencyKey,
            RecordActorType.Admin,
            adminUserId,
            transfer.Id));
        database.Transactions.Add(CreateTransaction(
            destination.UserId,
            destination.Id,
            transfer.Amount,
            "Transfer",
            isPeerTransfer ? "P2P Transfer" : "Internal Transfer",
            isPeerTransfer ? $"Transfer from {senderDisplayName}" : $"Transfer from {source.Nickname}",
            TransactionState.Posted,
            transfer.CorrelationId,
            transfer.IdempotencyKey,
            RecordActorType.Admin,
            adminUserId,
            transfer.Id));

        database.Notifications.Add(new NotificationItem
        {
            UserId = transfer.UserId,
            Severity = NotificationSeverity.Warning,
            Message = $"Transfer {transfer.Id.ToString("N")[..8]} was approved and posted."
        });
        if (isPeerTransfer)
        {
            database.Notifications.Add(new NotificationItem
            {
                UserId = destination.UserId,
                Severity = NotificationSeverity.Info,
                Message = $"You received {transfer.Amount:C} from {senderDisplayName}."
            });
        }

        AddAudit(
            database,
            adminUserId,
            UserRole.Admin,
            isPeerTransfer ? "approve_reviewed_peer_transfer" : "approve_reviewed_transfer",
            "transfer",
            transfer.Id.ToString("N"),
            $"State=Posted;DestinationUserId={destination.UserId:N}");
        AddDomainEvent(database, "TransferApproved", "transfer", transfer.Id.ToString("N"), transfer.UserId, transfer.CorrelationId, "State=Posted");
        RefreshLedgerState(database);
        CreateLowBalanceAlertIfNeeded(database, source);
        QueueTransferStatus(
            transfer,
            isPeerTransfer ? "Reviewed transfer posted to another user." : "Transfer approved and posted.");
        QueueAccountBalance(database, source, "Source account balance updated.");
        QueueAccountBalance(database, destination, "Destination account balance updated.");
        return OperationResult.Ok("Transfer approved and posted.");
    }

    private TransferActionResult ExecuteTransferFromSchedule(
        BankDatabase database,
        BankTransfer scheduledTransfer,
        TransferRequest request,
        DateTimeOffset now)
    {
        RefreshLedgerState(database);
        BankAccount? source = database.Accounts.FirstOrDefault(account =>
            account.Id == request.SourceAccountId &&
            account.UserId == scheduledTransfer.UserId);
        if (source is null || source.Status == BankAccountStatus.Frozen || request.Amount > source.AvailableBalance)
        {
            scheduledTransfer.State = TransferState.Cancelled;
            AddAudit(database, scheduledTransfer.UserId, UserRole.User, "scheduled_transfer_cancelled", "transfer", scheduledTransfer.Id.ToString("N"), "Insufficient funds or source frozen");
            return TransferActionResult.Fail("Scheduled transfer cancelled.");
        }

        if (scheduledTransfer.IsExternalAch)
        {
            string correlationId = GenerateCorrelationId();
            scheduledTransfer.State = TransferState.Pending;
            scheduledTransfer.SettlesUtc = now.AddMinutes(2);

            AppendBalancedLedger(
                database,
                source,
                GetOrCreateSystemAccount(database, SystemSettlementAccountKey, "ACH Settlement Clearing"),
                request.Amount,
                0m,
                "scheduled_external_transfer_hold",
                correlationId,
                request.IdempotencyKey ?? scheduledTransfer.IdempotencyKey,
                scheduledTransfer.CreatedBy,
                scheduledTransfer.CreatedByUserId,
                BuildTransferDescription(scheduledTransfer, database),
                scheduledTransfer.Id);
            database.Transactions.Add(CreateTransaction(
                scheduledTransfer.UserId,
                source.Id,
                -request.Amount,
                "Transfer",
                "ACH External",
                BuildTransferDescription(scheduledTransfer, database),
                TransactionState.Pending,
                correlationId,
                request.IdempotencyKey ?? scheduledTransfer.IdempotencyKey,
                scheduledTransfer.CreatedBy,
                scheduledTransfer.CreatedByUserId,
                scheduledTransfer.Id));
            RefreshLedgerState(database);
            CreateLowBalanceAlertIfNeeded(database, source);
            AddAudit(database, scheduledTransfer.UserId, UserRole.User, "scheduled_external_transfer_posted", "transfer", scheduledTransfer.Id.ToString("N"), $"Amount={request.Amount}");
            AddDomainEvent(
                database,
                "ScheduledTransferExecuted",
                "transfer",
                scheduledTransfer.Id.ToString("N"),
                scheduledTransfer.UserId,
                correlationId,
                $"Amount={request.Amount};State=Pending");
        }
        else
        {
            if (!scheduledTransfer.DestinationInternalAccountId.HasValue)
            {
                scheduledTransfer.State = TransferState.Cancelled;
                return TransferActionResult.Fail("Destination missing.");
            }

            BankAccount? destination = database.Accounts.FirstOrDefault(account =>
                account.Id == scheduledTransfer.DestinationInternalAccountId.Value &&
                !account.IsSystemAccount &&
                account.Type != BankAccountType.ExternalLinked);
            if (destination is null || destination.Status == BankAccountStatus.Frozen)
            {
                scheduledTransfer.State = TransferState.Cancelled;
                return TransferActionResult.Fail("Destination unavailable.");
            }

            bool isPeerTransfer = destination.UserId != scheduledTransfer.UserId;
            BankUser? sender = database.Users.FirstOrDefault(user => user.Id == scheduledTransfer.UserId);
            string senderDisplayName = sender is null ? "another user" : BuildDisplayName(sender);

            string correlationId = GenerateCorrelationId();
            scheduledTransfer.State = TransferState.Posted;
            scheduledTransfer.SettlesUtc = now;

            AppendBalancedLedger(
                database,
                source,
                destination,
                request.Amount,
                request.Amount,
                "scheduled_internal_transfer_posted",
                correlationId,
                request.IdempotencyKey ?? scheduledTransfer.IdempotencyKey,
                scheduledTransfer.CreatedBy,
                scheduledTransfer.CreatedByUserId,
                BuildTransferDescription(scheduledTransfer, database),
                scheduledTransfer.Id);

            database.Transactions.Add(CreateTransaction(
                scheduledTransfer.UserId,
                source.Id,
                -request.Amount,
                "Transfer",
                isPeerTransfer ? "P2P Transfer" : "Internal Transfer",
                isPeerTransfer
                    ? $"Scheduled transfer to {destination.Nickname}"
                    : BuildTransferDescription(scheduledTransfer, database),
                TransactionState.Posted,
                correlationId,
                request.IdempotencyKey ?? scheduledTransfer.IdempotencyKey,
                scheduledTransfer.CreatedBy,
                scheduledTransfer.CreatedByUserId,
                scheduledTransfer.Id));
            database.Transactions.Add(CreateTransaction(
                destination.UserId,
                destination.Id,
                request.Amount,
                "Transfer",
                isPeerTransfer ? "P2P Transfer" : "Internal Transfer",
                isPeerTransfer
                    ? $"Scheduled transfer from {senderDisplayName}"
                    : $"Transfer from {source.Nickname}",
                TransactionState.Posted,
                correlationId,
                request.IdempotencyKey ?? scheduledTransfer.IdempotencyKey,
                scheduledTransfer.CreatedBy,
                scheduledTransfer.CreatedByUserId,
                scheduledTransfer.Id));

            if (isPeerTransfer)
            {
                database.Notifications.Add(new NotificationItem
                {
                    UserId = destination.UserId,
                    Severity = NotificationSeverity.Info,
                    Message = $"You received {request.Amount:C} from {senderDisplayName} (scheduled transfer)."
                });
            }

            RefreshLedgerState(database);
            CreateLowBalanceAlertIfNeeded(database, source);
            AddAudit(
                database,
                scheduledTransfer.UserId,
                UserRole.User,
                isPeerTransfer ? "scheduled_peer_transfer_posted" : "scheduled_internal_transfer_posted",
                "transfer",
                scheduledTransfer.Id.ToString("N"),
                $"Amount={request.Amount};DestinationUserId={destination.UserId:N}");
            AddDomainEvent(
                database,
                "ScheduledTransferExecuted",
                "transfer",
                scheduledTransfer.Id.ToString("N"),
                scheduledTransfer.UserId,
                correlationId,
                isPeerTransfer
                    ? $"Amount={request.Amount};State=Posted;Type=Peer"
                    : $"Amount={request.Amount};State=Posted");
            QueueAccountBalance(database, source, "Scheduled transfer updated source balance.");
            QueueAccountBalance(database, destination, "Scheduled transfer updated destination balance.");
        }

        if (scheduledTransfer.Frequency == TransferFrequency.Weekly)
        {
            scheduledTransfer.State = TransferState.Scheduled;
            scheduledTransfer.NextRunUtc = now.AddDays(7);
        }
        else if (scheduledTransfer.Frequency == TransferFrequency.Monthly)
        {
            scheduledTransfer.State = TransferState.Scheduled;
            scheduledTransfer.NextRunUtc = now.AddMonths(1);
        }
        else
        {
            scheduledTransfer.NextRunUtc = null;
        }

        database.Notifications.Add(new NotificationItem
        {
            UserId = scheduledTransfer.UserId,
            Severity = NotificationSeverity.Success,
            Message = $"Scheduled transfer executed: {request.Amount:C}"
        });
        return TransferActionResult.Ok(scheduledTransfer.Id, scheduledTransfer.State, "Scheduled transfer executed.");
    }

    private static void GenerateMonthlyStatementRecords(BankDatabase database, DateTimeOffset now)
    {
        int year = now.Year;
        int month = now.Month;

        foreach (BankAccount account in database.Accounts.Where(account =>
                     account.Type != BankAccountType.ExternalLinked &&
                     !account.IsSystemAccount))
        {
            bool exists = database.Statements.Any(statement =>
                statement.UserId == account.UserId &&
                statement.AccountId == account.Id &&
                statement.Year == year &&
                statement.Month == month);

            if (exists)
            {
                continue;
            }

            database.Statements.Add(new StatementRecord
            {
                UserId = account.UserId,
                AccountId = account.Id,
                Year = year,
                Month = month,
                PdfFileName = $"statement-{year:D4}-{month:D2}-{MaskAccountNumber(account.AccountNumber)}.pdf",
                CsvFileName = $"statement-{year:D4}-{month:D2}-{MaskAccountNumber(account.AccountNumber)}.csv"
            });
        }
    }

    private static List<BankTransaction> FilterStatementTransactions(
        BankDatabase database,
        Guid userId,
        Guid accountId,
        int year,
        int month)
    {
        return database.Transactions
            .Where(transaction =>
                transaction.UserId == userId &&
                transaction.AccountId == accountId &&
                transaction.CreatedUtc.Year == year &&
                transaction.CreatedUtc.Month == month)
            .OrderBy(transaction => transaction.CreatedUtc)
            .ToList();
    }

    private static string BuildCsv(List<BankTransaction> rows)
    {
        StringBuilder csv = new();
        csv.AppendLine("Timestamp,State,Merchant,Category,Amount,Description,Reference");
        foreach (BankTransaction row in rows)
        {
            csv.AppendLine(string.Join(",",
                EscapeCsv(row.CreatedUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)),
                EscapeCsv(row.State.ToString()),
                EscapeCsv(row.MerchantName),
                EscapeCsv(row.Category),
                EscapeCsv(row.Amount.ToString("F2", CultureInfo.InvariantCulture)),
                EscapeCsv(row.Description),
                EscapeCsv(row.ReferenceCode)));
        }

        return csv.ToString();
    }

    private static string BuildPseudoPdfText(BankAccount account, List<BankTransaction> rows, int year, int month)
    {
        StringBuilder text = new();
        text.AppendLine("%PDF-FAKE-1.0");
        text.AppendLine($"Statement for {account.Nickname}");
        text.AppendLine($"Account {MaskAccountNumber(account.AccountNumber)} | Routing {account.RoutingNumber}");
        text.AppendLine($"Period: {year:D4}-{month:D2}");
        text.AppendLine("-----------------------------------------------");
        foreach (BankTransaction row in rows)
        {
            text.AppendLine($"{row.CreatedUtc:yyyy-MM-dd} {row.State,-7} {row.Amount,10:C} {row.MerchantName} | {row.Description}");
        }

        text.AppendLine("-----------------------------------------------");
        text.AppendLine($"Ending balance: {account.AvailableBalance:C}");
        return text.ToString();
    }

    private static string EscapeCsv(string text)
    {
        string safe = (text ?? string.Empty).Replace("\"", "\"\"");
        return $"\"{safe}\"";
    }

    private readonly record struct FraudAssessment(bool ShouldReview, int Score, string Flags);

    private readonly record struct PeerDestinationResolution(
        bool IsSuccess,
        Guid? DestinationAccountId,
        string Message)
    {
        public static PeerDestinationResolution Ok(Guid destinationAccountId) =>
            new(true, destinationAccountId, string.Empty);

        public static PeerDestinationResolution Fail(string message) =>
            new(false, null, message);
    }
}

public sealed record OperationResult(bool IsSuccess, string Message)
{
    public static OperationResult Ok(string message) => new(true, message);
    public static OperationResult Fail(string message) => new(false, message);
}

public sealed record SignupRequest(
    string Email,
    string Password,
    string FirstName,
    string LastName,
    string? Address,
    string? Phone);

public sealed record LoginCompleteResult(
    bool IsSuccess,
    string Message,
    Guid? UserId,
    UserRole Role,
    string FirstName,
    string? AccessToken,
    string? RefreshToken)
{
    public static LoginCompleteResult Ok(
        Guid userId,
        UserRole role,
        string firstName,
        string accessToken,
        string refreshToken) =>
        new(true, "Login completed.", userId, role, firstName, accessToken, refreshToken);

    public static LoginCompleteResult Fail(string message) =>
        new(false, message, null, UserRole.User, string.Empty, null, null);
}

public sealed record RefreshResult(bool IsSuccess, string Message, string? AccessToken, string? RefreshToken)
{
    public static RefreshResult Ok(string accessToken, string refreshToken) =>
        new(true, "Token refreshed.", accessToken, refreshToken);

    public static RefreshResult Fail(string message) =>
        new(false, message, null, null);
}

public sealed record TransferRequest
{
    public Guid SourceAccountId { get; init; }
    public Guid? DestinationInternalAccountId { get; init; }
    public Guid? DestinationExternalAccountId { get; init; }
    public decimal Amount { get; init; }
    public string? Memo { get; init; }
    public DateTimeOffset? ScheduledForUtc { get; init; }
    public TransferFrequency Frequency { get; init; } = TransferFrequency.OneTime;
    public string? IdempotencyKey { get; init; }
}

public sealed record PeerTransferByEmailRequest
{
    public Guid SourceAccountId { get; init; }
    public string RecipientEmail { get; init; } = string.Empty;
    public BankAccountType? PreferredDestinationAccountType { get; init; }
    public decimal Amount { get; init; }
    public string? Memo { get; init; }
    public DateTimeOffset? ScheduledForUtc { get; init; }
    public TransferFrequency Frequency { get; init; } = TransferFrequency.OneTime;
    public string? IdempotencyKey { get; init; }
}

public sealed record TransferActionResult(
    bool IsSuccess,
    string Message,
    Guid? TransferId,
    TransferState? State)
{
    public static TransferActionResult Ok(Guid transferId, TransferState state, string message) =>
        new(true, message, transferId, state);

    public static TransferActionResult Fail(string message) =>
        new(false, message, null, null);
}

public sealed record AccountOpenResult(
    bool IsSuccess,
    string Message,
    Guid? AccountId)
{
    public static AccountOpenResult Ok(Guid accountId, string message) =>
        new(true, message, accountId);

    public static AccountOpenResult Fail(string message) =>
        new(false, message, null);
}

public sealed record CategorySpendItem(string Category, decimal Amount);

public sealed record BudgetProgressItem(
    Guid BudgetId,
    string Category,
    decimal MonthlyLimit,
    decimal Spent,
    decimal PercentUsed);

public sealed record SavingsGoalProgressItem(
    Guid GoalId,
    string Name,
    decimal TargetAmount,
    decimal CurrentAmount,
    decimal PercentReached);

public sealed record DashboardSnapshot(
    BankUser User,
    IReadOnlyList<BankAccount> Accounts,
    IReadOnlyList<BankTransaction> Transactions,
    decimal InflowThisMonth,
    decimal OutflowThisMonth,
    decimal OutflowLastMonth,
    decimal TotalBalance,
    IReadOnlyList<CategorySpendItem> CategorySpending,
    IReadOnlyList<BudgetProgressItem> BudgetProgress,
    IReadOnlyList<SavingsGoalProgressItem> Goals,
    IReadOnlyList<NotificationItem> Notifications,
    IReadOnlyList<BankTransfer> UpcomingTransfers,
    int DaysWindow);

public sealed record LedgerFilter(
    Guid? AccountId,
    string? Search,
    string? Category,
    TransactionState? State,
    DateTimeOffset? FromUtc,
    DateTimeOffset? ToUtc);

public sealed record LedgerPageResult(
    IReadOnlyList<BankTransaction> Transactions,
    IReadOnlyList<string> Categories,
    IReadOnlyList<BankAccount> Accounts);

public sealed record StatementExport(
    string FileName,
    string ContentType,
    byte[] Bytes);

public sealed record AdminUserView(
    Guid UserId,
    string Email,
    string FirstName,
    string LastName,
    UserRole Role,
    int AccountCount,
    decimal TotalBalance);

public sealed record AdminAccountView(
    Guid AccountId,
    Guid UserId,
    string Nickname,
    BankAccountType Type,
    BankAccountStatus Status,
    decimal AvailableBalance,
    string MaskedAccountNumber,
    string RoutingNumber);

public sealed record AdminFraudTransferView(
    Guid TransferId,
    Guid UserId,
    string SourceAccountName,
    decimal Amount,
    bool IsExternalAch,
    TransferState State,
    int FraudScore,
    string FraudFlags,
    DateTimeOffset RequestedUtc);

public sealed record AdminDomainEventView(
    DateTimeOffset CreatedUtc,
    string EventType,
    string EntityType,
    string EntityId,
    Guid? UserId,
    string Metadata);

public sealed record AdminSnapshot(
    IReadOnlyList<AdminUserView> Users,
    IReadOnlyList<AdminAccountView> Accounts,
    IReadOnlyList<AdminFraudTransferView> FraudTransfers,
    IReadOnlyList<DisputeTicket> Disputes,
    IReadOnlyList<AuditLogEntry> AuditLogs,
    IReadOnlyList<AdminDomainEventView> DomainEvents);
