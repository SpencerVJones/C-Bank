using AtmMachine.WebUI.Banking.Models;
using AtmMachine.WebUI.Banking.Services;
using AtmMachine.WebUI.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AtmMachine.WebUI.Pages;

[EnableRateLimiting("transfers")]
public sealed class TransfersModel : AuthenticatedPageModel
{
    private readonly BankingService _bankingService;

    public TransfersModel(BankingService bankingService)
    {
        _bankingService = bankingService;
    }

    public DashboardSnapshot? Snapshot { get; private set; }
    public IReadOnlyList<LinkedExternalAccount> ExternalAccounts { get; private set; } = Array.Empty<LinkedExternalAccount>();
    public IReadOnlyList<TransferRecipientAccount> RecipientAccounts { get; private set; } = Array.Empty<TransferRecipientAccount>();

    [TempData]
    public string? SuccessMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!TryGetAuthenticatedUser(out Guid userId))
        {
            return RedirectToLoginPage();
        }

        Snapshot = await _bankingService.GetDashboardSnapshotAsync(userId, 30);
        ExternalAccounts = await _bankingService.GetLinkedExternalAccountsAsync(userId);
        RecipientAccounts = await _bankingService.GetTransferRecipientsAsync(userId);
        return Snapshot is null ? RedirectToLoginPage() : Page();
    }

    public async Task<IActionResult> OnPostLinkExternalAsync(string bankName, string nickname, string accountMask)
    {
        if (!TryGetAuthenticatedUser(out Guid userId))
        {
            return RedirectToLoginPage();
        }

        OperationResult result = await _bankingService.LinkExternalAccountAsync(userId, bankName, nickname, accountMask);
        if (result.IsSuccess)
        {
            SuccessMessage = result.Message;
        }
        else
        {
            ErrorMessage = result.Message;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostInternalAsync(
        Guid sourceAccountId,
        Guid destinationAccountId,
        decimal amount,
        string? memo,
        DateTimeOffset? scheduledForUtc,
        TransferFrequency frequency)
    {
        if (!TryGetAuthenticatedUser(out Guid userId, out UserRole role))
        {
            return RedirectToLoginPage();
        }

        TransferActionResult result = await _bankingService.CreateTransferAsync(
            userId,
            new TransferRequest
            {
                SourceAccountId = sourceAccountId,
                DestinationInternalAccountId = destinationAccountId,
                Amount = amount,
                Memo = memo,
                ScheduledForUtc = scheduledForUtc,
                Frequency = frequency,
                IdempotencyKey = Guid.NewGuid().ToString("N")
            },
            role);

        if (result.IsSuccess)
        {
            SuccessMessage = result.Message;
        }
        else
        {
            ErrorMessage = result.Message;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostExternalAsync(
        Guid sourceAccountId,
        Guid externalAccountId,
        decimal amount,
        string? memo,
        DateTimeOffset? scheduledForUtc,
        TransferFrequency frequency)
    {
        if (!TryGetAuthenticatedUser(out Guid userId, out UserRole role))
        {
            return RedirectToLoginPage();
        }

        TransferActionResult result = await _bankingService.CreateTransferAsync(
            userId,
            new TransferRequest
            {
                SourceAccountId = sourceAccountId,
                DestinationExternalAccountId = externalAccountId,
                Amount = amount,
                Memo = memo,
                ScheduledForUtc = scheduledForUtc,
                Frequency = frequency,
                IdempotencyKey = Guid.NewGuid().ToString("N")
            },
            role);

        if (result.IsSuccess)
        {
            SuccessMessage = result.Message;
        }
        else
        {
            ErrorMessage = result.Message;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPeerAsync(
        Guid sourceAccountId,
        Guid destinationAccountId,
        decimal amount,
        string? memo,
        DateTimeOffset? scheduledForUtc,
        TransferFrequency frequency)
    {
        if (!TryGetAuthenticatedUser(out Guid userId, out UserRole role))
        {
            return RedirectToLoginPage();
        }

        TransferActionResult result = await _bankingService.CreateTransferAsync(
            userId,
            new TransferRequest
            {
                SourceAccountId = sourceAccountId,
                DestinationInternalAccountId = destinationAccountId,
                Amount = amount,
                Memo = memo,
                ScheduledForUtc = scheduledForUtc,
                Frequency = frequency,
                IdempotencyKey = Guid.NewGuid().ToString("N")
            },
            role);

        if (result.IsSuccess)
        {
            SuccessMessage = result.Message;
        }
        else
        {
            ErrorMessage = result.Message;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPeerByEmailAsync(
        Guid sourceAccountId,
        string recipientEmail,
        string? recipientAccountType,
        decimal amount,
        string? memo,
        DateTimeOffset? scheduledForUtc,
        TransferFrequency frequency)
    {
        if (!TryGetAuthenticatedUser(out Guid userId, out UserRole role))
        {
            return RedirectToLoginPage();
        }

        BankAccountType? preferredType = null;
        if (!string.IsNullOrWhiteSpace(recipientAccountType))
        {
            bool parsed = Enum.TryParse<BankAccountType>(
                recipientAccountType,
                ignoreCase: true,
                out BankAccountType candidateType);

            if (!parsed || candidateType == BankAccountType.ExternalLinked)
            {
                ErrorMessage = "Recipient account type must be Checking or Savings.";
                return RedirectToPage();
            }

            preferredType = candidateType;
        }

        TransferActionResult result = await _bankingService.CreatePeerTransferByEmailAsync(
            userId,
            new PeerTransferByEmailRequest
            {
                SourceAccountId = sourceAccountId,
                RecipientEmail = recipientEmail,
                PreferredDestinationAccountType = preferredType,
                Amount = amount,
                Memo = memo,
                ScheduledForUtc = scheduledForUtc,
                Frequency = frequency,
                IdempotencyKey = Guid.NewGuid().ToString("N")
            },
            role);

        if (result.IsSuccess)
        {
            SuccessMessage = result.Message;
        }
        else
        {
            ErrorMessage = result.Message;
        }

        return RedirectToPage();
    }
}
