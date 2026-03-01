using AtmMachine.WebUI.Banking.Models;
using AtmMachine.WebUI.Banking.Services;
using AtmMachine.WebUI.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AtmMachine.WebUI.Pages;

public sealed class AdminModel : AuthenticatedPageModel
{
    private readonly BankingService _bankingService;

    public AdminModel(BankingService bankingService)
    {
        _bankingService = bankingService;
    }

    public AdminSnapshot? Snapshot { get; private set; }

    [TempData]
    public string? SuccessMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!TryGetAuthenticatedUser(out _, out UserRole role) || role != UserRole.Admin)
        {
            return RedirectToDashboardPage();
        }

        Snapshot = await _bankingService.GetAdminSnapshotAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostToggleFreezeAsync(Guid accountId, bool freeze)
    {
        if (!TryGetAuthenticatedUser(out Guid userId, out UserRole role) || role != UserRole.Admin)
        {
            return RedirectToDashboardPage();
        }

        OperationResult result = await _bankingService.AdminFreezeAccountAsync(userId, accountId, freeze);
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

    public async Task<IActionResult> OnPostManualAdjustmentAsync(Guid accountId, decimal amount, string? note)
    {
        if (!TryGetAuthenticatedUser(out Guid userId, out UserRole role) || role != UserRole.Admin)
        {
            return RedirectToDashboardPage();
        }

        OperationResult result = await _bankingService.AdminManualAdjustmentAsync(
            userId,
            accountId,
            amount,
            note ?? string.Empty);

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

    public async Task<IActionResult> OnPostReviewDisputeAsync(Guid disputeId, string status, string? adminNotes)
    {
        if (!TryGetAuthenticatedUser(out Guid userId, out UserRole role) || role != UserRole.Admin)
        {
            return RedirectToDashboardPage();
        }

        if (!Enum.TryParse<DisputeStatus>(status, true, out DisputeStatus parsedStatus))
        {
            ErrorMessage = "Invalid dispute status.";
            return RedirectToPage();
        }

        OperationResult result = await _bankingService.AdminReviewDisputeAsync(
            userId,
            disputeId,
            parsedStatus,
            adminNotes ?? string.Empty);

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

    public async Task<IActionResult> OnPostReviewTransferAsync(Guid transferId, bool approve, string? adminNotes)
    {
        if (!TryGetAuthenticatedUser(out Guid userId, out UserRole role) || role != UserRole.Admin)
        {
            return RedirectToDashboardPage();
        }

        OperationResult result = await _bankingService.AdminReviewTransferAsync(
            userId,
            transferId,
            approve,
            adminNotes ?? string.Empty);

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
