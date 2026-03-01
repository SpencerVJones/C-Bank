using AtmMachine.WebUI.Banking.Models;
using AtmMachine.WebUI.Banking.Services;
using AtmMachine.WebUI.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AtmMachine.WebUI.Pages;

public sealed class StatementsModel : AuthenticatedPageModel
{
    private readonly BankingService _bankingService;

    public StatementsModel(BankingService bankingService)
    {
        _bankingService = bankingService;
    }

    [BindProperty(SupportsGet = true)]
    public Guid? AccountId { get; set; }

    [BindProperty(SupportsGet = true)]
    public int Year { get; set; } = DateTime.UtcNow.Year;

    [BindProperty(SupportsGet = true)]
    public int Month { get; set; } = DateTime.UtcNow.Month;

    public DashboardSnapshot? Snapshot { get; private set; }
    public BankAccount? Account { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!TryGetAuthenticatedUser(out Guid userId))
        {
            return RedirectToLoginPage();
        }

        Snapshot = await _bankingService.GetDashboardSnapshotAsync(userId, 365);
        if (Snapshot is null)
        {
            return RedirectToLoginPage();
        }

        Account = ResolveAccount(Snapshot.Accounts, AccountId);
        if (Account is null)
        {
            AccountId = null;
        }
        else
        {
            AccountId = Account.Id;
        }

        return Page();
    }

    public async Task<IActionResult> OnGetCsvAsync(Guid accountId, int year, int month)
    {
        if (!TryGetAuthenticatedUser(out Guid userId))
        {
            return RedirectToLoginPage();
        }

        StatementExport? export = await _bankingService.ExportStatementCsvAsync(userId, accountId, year, month);
        if (export is null)
        {
            return RedirectToPage(new { accountId, year, month });
        }

        return File(export.Bytes, export.ContentType, export.FileName);
    }

    public async Task<IActionResult> OnGetPdfAsync(Guid accountId, int year, int month)
    {
        if (!TryGetAuthenticatedUser(out Guid userId))
        {
            return RedirectToLoginPage();
        }

        StatementExport? export = await _bankingService.ExportStatementPdfAsync(userId, accountId, year, month);
        if (export is null)
        {
            return RedirectToPage(new { accountId, year, month });
        }

        return File(export.Bytes, export.ContentType, export.FileName);
    }

    private static BankAccount? ResolveAccount(IReadOnlyList<BankAccount> accounts, Guid? accountId)
    {
        if (accounts.Count == 0)
        {
            return null;
        }

        if (!accountId.HasValue)
        {
            return accounts[0];
        }

        return accounts.FirstOrDefault(account => account.Id == accountId.Value) ?? accounts[0];
    }
}
