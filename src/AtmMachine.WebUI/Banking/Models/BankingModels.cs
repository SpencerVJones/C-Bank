namespace AtmMachine.WebUI.Banking.Models;

public enum UserRole
{
    User = 1,
    Admin = 2
}

public enum BankAccountType
{
    Checking = 1,
    Savings = 2,
    ExternalLinked = 3
}

public enum BankAccountStatus
{
    Active = 1,
    Frozen = 2
}

public enum TransactionState
{
    Pending = 1,
    Posted = 2
}

public enum TransferState
{
    Scheduled = 1,
    Pending = 2,
    Posted = 3,
    Cancelled = 4,
    UnderReview = 5
}

public enum TransferFrequency
{
    OneTime = 1,
    Weekly = 2,
    Monthly = 3
}

public enum NotificationSeverity
{
    Info = 1,
    Success = 2,
    Warning = 3,
    Critical = 4
}

public enum DisputeStatus
{
    Open = 1,
    InReview = 2,
    Resolved = 3,
    Rejected = 4
}

public enum RecordActorType
{
    User = 1,
    Admin = 2,
    System = 3
}

public sealed class BankUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.User;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginUtc { get; set; }
    public int FailedLoginAttempts { get; set; }
    public DateTimeOffset? LockedUntilUtc { get; set; }
    public UserSettings Settings { get; set; } = new();
    public List<RefreshSession> RefreshSessions { get; set; } = new();
}

public sealed class UserSettings
{
    public bool EmailNotificationsEnabled { get; set; } = true;
    public bool SecurityAlertsEnabled { get; set; } = true;
    public bool MarketingEmailsEnabled { get; set; }
}

public sealed class RefreshSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RefreshToken { get; set; } = string.Empty;
    public DateTimeOffset IssuedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
    public string DeviceId { get; set; } = string.Empty;
}

public sealed class BankAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public BankAccountType Type { get; set; }
    public string Nickname { get; set; } = string.Empty;
    public string AccountNumber { get; set; } = string.Empty;
    public string RoutingNumber { get; set; } = string.Empty;
    public BankAccountStatus Status { get; set; } = BankAccountStatus.Active;
    public decimal AvailableBalance { get; set; }
    public decimal LedgerBalance { get; set; }
    public bool IsSystemAccount { get; set; }
    public string InternalKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class LinkedExternalAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string BankName { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public string AccountMask { get; set; } = string.Empty;
    public string RoutingNumber { get; set; } = string.Empty;
    public bool Verified { get; set; } = true;
    public DateTimeOffset LinkedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record TransferRecipientAccount(
    Guid AccountId,
    Guid UserId,
    string RecipientName,
    string RecipientEmail,
    string AccountNickname,
    string MaskedAccountNumber,
    BankAccountType AccountType);

public sealed class BankTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid AccountId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PostedUtc { get; set; }
    public TransactionState State { get; set; } = TransactionState.Posted;
    public decimal Amount { get; set; }
    public string Category { get; set; } = "General";
    public string MerchantName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ReceiptNote { get; set; } = string.Empty;
    public string ReferenceCode { get; set; } = string.Empty;
    public string Counterparty { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public RecordActorType CreatedBy { get; set; } = RecordActorType.System;
    public Guid? CreatedByUserId { get; set; }
    public Guid? TransferId { get; set; }
}

public sealed class BankTransfer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid SourceAccountId { get; set; }
    public Guid? DestinationInternalAccountId { get; set; }
    public Guid? DestinationExternalAccountId { get; set; }
    public decimal Amount { get; set; }
    public string Memo { get; set; } = string.Empty;
    public bool IsExternalAch { get; set; }
    public TransferState State { get; set; } = TransferState.Scheduled;
    public DateTimeOffset RequestedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ScheduledUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SettlesUtc { get; set; }
    public TransferFrequency Frequency { get; set; } = TransferFrequency.OneTime;
    public DateTimeOffset? NextRunUtc { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public RecordActorType CreatedBy { get; set; } = RecordActorType.User;
    public Guid? CreatedByUserId { get; set; }
    public int FraudScore { get; set; }
    public string FraudFlags { get; set; } = string.Empty;
    public DateTimeOffset? ReviewedUtc { get; set; }
    public Guid? ReviewedByUserId { get; set; }
    public string ReviewNotes { get; set; } = string.Empty;
}

public sealed class LedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public Guid UserId { get; set; }
    public Guid? TransferId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public decimal AvailableDelta { get; set; }
    public decimal LedgerDelta { get; set; }
    public string EntryType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public RecordActorType CreatedBy { get; set; } = RecordActorType.System;
    public Guid? CreatedByUserId { get; set; }
}

public sealed class BudgetRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string Category { get; set; } = string.Empty;
    public decimal MonthlyLimit { get; set; }
}

public sealed class SavingsGoal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid SavingsAccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal TargetAmount { get; set; }
    public decimal CurrentAmount { get; set; }
}

public sealed class NotificationItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public NotificationSeverity Severity { get; set; } = NotificationSeverity.Info;
    public string Message { get; set; } = string.Empty;
    public bool IsRead { get; set; }
}

public sealed class LoginRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string IpAddress { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public bool IsNewDevice { get; set; }
}

public sealed class DeviceRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public DateTimeOffset FirstSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool Trusted { get; set; }
}

public sealed class DisputeTicket
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid TransactionId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DisputeStatus Status { get; set; } = DisputeStatus.Open;
    public string AdminNotes { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AuditLogEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ActorUserId { get; set; }
    public UserRole ActorRole { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Metadata { get; set; } = string.Empty;
}

public sealed class StatementRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid AccountId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public DateTimeOffset GeneratedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string PdfFileName { get; set; } = string.Empty;
    public string CsvFileName { get; set; } = string.Empty;
}

public sealed class IdempotencyRecord
{
    public string Key { get; set; } = string.Empty;
    public Guid TransferId { get; set; }
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DomainEventRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string EventType { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public Guid? UserId { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public string Metadata { get; set; } = string.Empty;
}

public sealed class BankDatabase
{
    public List<BankUser> Users { get; set; } = new();
    public List<BankAccount> Accounts { get; set; } = new();
    public List<LinkedExternalAccount> LinkedExternalAccounts { get; set; } = new();
    public List<BankTransaction> Transactions { get; set; } = new();
    public List<LedgerEntry> LedgerEntries { get; set; } = new();
    public List<BankTransfer> Transfers { get; set; } = new();
    public List<BudgetRule> Budgets { get; set; } = new();
    public List<SavingsGoal> Goals { get; set; } = new();
    public List<NotificationItem> Notifications { get; set; } = new();
    public List<LoginRecord> LoginHistory { get; set; } = new();
    public List<DeviceRecord> Devices { get; set; } = new();
    public List<DisputeTicket> Disputes { get; set; } = new();
    public List<AuditLogEntry> AuditLogs { get; set; } = new();
    public List<StatementRecord> Statements { get; set; } = new();
    public List<IdempotencyRecord> IdempotencyKeys { get; set; } = new();
    public List<DomainEventRecord> DomainEvents { get; set; } = new();
}
