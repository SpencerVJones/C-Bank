using AtmMachine.WebUI.Banking.Models;
using AtmMachine.WebUI.Banking.Services;
using AtmMachine.WebUI.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AtmMachine.WebUI.Pages;

public sealed class TransactionsModel : AuthenticatedPageModel
{
    private readonly BankingService _bankingService;

    public TransactionsModel(BankingService bankingService)
    {
        _bankingService = bankingService;
    }

    [BindProperty(SupportsGet = true)]
    public Guid? AccountId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Category { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? State { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTimeOffset? FromUtc { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTimeOffset? ToUtc { get; set; }

    public LedgerPageResult? Ledger { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!TryGetAuthenticatedUser(out Guid userId))
        {
            return RedirectToLoginPage();
        }

        TransactionState? parsedState = Enum.TryParse<TransactionState>(State, true, out TransactionState current)
            ? current
            : null;

        Ledger = await _bankingService.GetLedgerAsync(
            userId,
            new LedgerFilter(AccountId, Search, Category, parsedState, FromUtc, ToUtc));

        return Ledger is null ? RedirectToLoginPage() : Page();
    }
}
