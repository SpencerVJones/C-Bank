using AtmMachine.WebUI.Banking.Models;
using AtmMachine.WebUI.Banking.Services;
using AtmMachine.WebUI.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AtmMachine.WebUI.Pages;

public sealed class DashboardModel : AuthenticatedPageModel
{
    private readonly BankingService _bankingService;

    public DashboardModel(BankingService bankingService)
    {
        _bankingService = bankingService;
    }

    [BindProperty(SupportsGet = true)]
    public int Days { get; set; } = 30;

    [BindProperty(SupportsGet = true)]
    public Guid? AccountId { get; set; }

    public DashboardSnapshot? Snapshot { get; private set; }
    public BankAccount? ActiveAccount { get; private set; }
    public bool IsAdmin { get; private set; }

    [TempData]
    public string? SuccessMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!TryGetAuthenticatedUser(out Guid userId, out UserRole role))
        {
            return RedirectToLoginPage();
        }

        Snapshot = await _bankingService.GetDashboardSnapshotAsync(userId, Days);
        if (Snapshot is null)
        {
            return RedirectToLoginPage();
        }

        IsAdmin = role == UserRole.Admin;
        ActiveAccount = ResolveActiveAccount(Snapshot.Accounts, AccountId);
        return Page();
    }

    public async Task<IActionResult> OnPostMarkReadAsync(Guid notificationId, int days, Guid? accountId)
    {
        if (!TryGetAuthenticatedUser(out Guid userId))
        {
            return RedirectToLoginPage();
        }

        await _bankingService.MarkNotificationReadAsync(userId, notificationId);
        return RedirectToPage(new { days, accountId });
    }

    public async Task<IActionResult> OnPostCreateBudgetAsync(string category, decimal monthlyLimit, int days, Guid? accountId)
    {
        if (!TryGetAuthenticatedUser(out Guid userId))
        {
            return RedirectToLoginPage();
        }

        OperationResult result = await _bankingService.CreateBudgetAsync(userId, category, monthlyLimit);
        if (result.IsSuccess)
        {
            SuccessMessage = result.Message;
        }
        else
        {
            ErrorMessage = result.Message;
        }

        return RedirectToPage(new { days, accountId });
    }

    public async Task<IActionResult> OnPostCreateGoalAsync(Guid savingsAccountId, string name, decimal targetAmount, int days, Guid? accountId)
    {
        if (!TryGetAuthenticatedUser(out Guid userId))
        {
            return RedirectToLoginPage();
        }

        OperationResult result = await _bankingService.CreateGoalAsync(userId, savingsAccountId, name, targetAmount);
        if (result.IsSuccess)
        {
            SuccessMessage = result.Message;
        }
        else
        {
            ErrorMessage = result.Message;
        }

        return RedirectToPage(new { days, accountId });
    }

    public async Task<IActionResult> OnPostContributeGoalAsync(Guid goalId, Guid sourceAccountId, decimal amount, int days, Guid? accountId)
    {
        if (!TryGetAuthenticatedUser(out Guid userId))
        {
            return RedirectToLoginPage();
        }

        OperationResult result = await _bankingService.ContributeGoalAsync(userId, goalId, sourceAccountId, amount);
        if (result.IsSuccess)
        {
            SuccessMessage = result.Message;
        }
        else
        {
            ErrorMessage = result.Message;
        }

        return RedirectToPage(new { days, accountId });
    }

    public async Task<IActionResult> OnPostAddFundsAsync(
        Guid accountId,
        decimal amount,
        string source,
        string? note,
        int days,
        Guid? selectedAccountId)
    {
        if (!TryGetAuthenticatedUser(out Guid userId))
        {
            return RedirectToLoginPage();
        }

        OperationResult result = await _bankingService.AddFundsAsync(
            userId,
            accountId,
            amount,
            source,
            note ?? string.Empty);

        if (result.IsSuccess)
        {
            SuccessMessage = result.Message;
        }
        else
        {
            ErrorMessage = result.Message;
        }

        Guid redirectAccount = selectedAccountId ?? accountId;
        return RedirectToPage(new { days, accountId = redirectAccount });
    }

    public async Task<IActionResult> OnPostSpendFundsAsync(
        Guid accountId,
        decimal amount,
        string merchantName,
        string category,
        string? note,
        int days,
        Guid? selectedAccountId)
    {
        if (!TryGetAuthenticatedUser(out Guid userId))
        {
            return RedirectToLoginPage();
        }

        OperationResult result = await _bankingService.SpendFundsAsync(
            userId,
            accountId,
            amount,
            merchantName,
            category,
            note ?? string.Empty);

        if (result.IsSuccess)
        {
            SuccessMessage = result.Message;
        }
        else
        {
            ErrorMessage = result.Message;
        }

        Guid redirectAccount = selectedAccountId ?? accountId;
        return RedirectToPage(new { days, accountId = redirectAccount });
    }

    private static BankAccount? ResolveActiveAccount(IReadOnlyList<BankAccount> accounts, Guid? accountId)
    {
        if (accounts.Count == 0)
        {
            return null;
        }

        if (accountId.HasValue)
        {
            return accounts.FirstOrDefault(account => account.Id == accountId.Value) ?? accounts[0];
        }

        return accounts[0];
    }
}
