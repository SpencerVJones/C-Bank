using AtmMachine.WebUI.Banking.Models;
using AtmMachine.WebUI.Banking.Services;
using AtmMachine.WebUI.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AtmMachine.WebUI.Pages;

public sealed class AccountsModel : AuthenticatedPageModel
{
    private readonly BankingService _bankingService;

    public AccountsModel(BankingService bankingService)
    {
        _bankingService = bankingService;
    }

    [BindProperty(SupportsGet = true)]
    public Guid? AccountId { get; set; }

    public DashboardSnapshot? Snapshot { get; private set; }
    public BankAccount? SelectedAccount { get; private set; }

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

        Snapshot = await _bankingService.GetDashboardSnapshotAsync(userId, 90);
        if (Snapshot is null)
        {
            return RedirectToLoginPage();
        }

        SelectedAccount = ResolveSelectedAccount(Snapshot.Accounts, AccountId);
        return Page();
    }

    public async Task<IActionResult> OnPostToggleFreezeAsync(Guid accountId, bool freeze)
    {
        if (!TryGetAuthenticatedUser(out Guid userId, out UserRole role))
        {
            return RedirectToLoginPage();
        }

        OperationResult result = await _bankingService.ToggleFreezeAccountAsync(
            userId,
            role,
            accountId,
            freeze);

        if (result.IsSuccess)
        {
            SuccessMessage = result.Message;
        }
        else
        {
            ErrorMessage = result.Message;
        }

        return RedirectToPage(new { accountId });
    }

    public async Task<IActionResult> OnPostOpenAccountAsync(
        BankAccountType accountType,
        string nickname,
        decimal openingDeposit,
        Guid? fundingSourceAccountId,
        Guid? selectedAccountId)
    {
        if (!TryGetAuthenticatedUser(out Guid userId))
        {
            return RedirectToLoginPage();
        }

        AccountOpenResult result = await _bankingService.OpenAccountAsync(
            userId,
            accountType,
            nickname,
            fundingSourceAccountId,
            openingDeposit);

        if (result.IsSuccess)
        {
            SuccessMessage = result.Message;
        }
        else
        {
            ErrorMessage = result.Message;
        }

        Guid? redirectAccountId = result.IsSuccess
            ? result.AccountId
            : selectedAccountId ?? fundingSourceAccountId;

        return RedirectToPage(new { accountId = redirectAccountId });
    }

    private static BankAccount? ResolveSelectedAccount(IReadOnlyList<BankAccount> accounts, Guid? accountId)
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
