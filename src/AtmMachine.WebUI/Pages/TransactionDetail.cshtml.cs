using AtmMachine.WebUI.Banking.Models;
using AtmMachine.WebUI.Banking.Services;
using AtmMachine.WebUI.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AtmMachine.WebUI.Pages;

public sealed class TransactionDetailModel : AuthenticatedPageModel
{
    private readonly BankingService _bankingService;

    public TransactionDetailModel(BankingService bankingService)
    {
        _bankingService = bankingService;
    }

    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    [BindProperty]
    public string ReceiptNote { get; set; } = string.Empty;

    [BindProperty]
    public string DisputeReason { get; set; } = string.Empty;

    [BindProperty]
    public string DisputeNotes { get; set; } = string.Empty;

    public BankTransaction? Transaction { get; private set; }

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

        Transaction = await _bankingService.GetTransactionAsync(userId, Id);
        if (Transaction is null)
        {
            return RedirectToPage("/Transactions");
        }

        ReceiptNote = Transaction.ReceiptNote;
        return Page();
    }

    public async Task<IActionResult> OnPostSaveReceiptAsync()
    {
        if (!TryGetAuthenticatedUser(out Guid userId))
        {
            return RedirectToLoginPage();
        }
        if (Id == Guid.Empty)
        {
            ErrorMessage = "Transaction reference is missing.";
            return RedirectToPage("/Transactions");
        }

        OperationResult result = await _bankingService.SaveReceiptNoteAsync(userId, Id, ReceiptNote);
        if (result.IsSuccess)
        {
            SuccessMessage = result.Message;
        }
        else
        {
            ErrorMessage = result.Message;
        }

        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDisputeAsync()
    {
        if (!TryGetAuthenticatedUser(out Guid userId))
        {
            return RedirectToLoginPage();
        }
        if (Id == Guid.Empty)
        {
            ErrorMessage = "Transaction reference is missing.";
            return RedirectToPage("/Transactions");
        }

        OperationResult result = await _bankingService.CreateDisputeAsync(userId, Id, DisputeReason, DisputeNotes);
        if (result.IsSuccess)
        {
            SuccessMessage = result.Message;
        }
        else
        {
            ErrorMessage = result.Message;
        }

        return RedirectToPage(new { id = Id });
    }
}
