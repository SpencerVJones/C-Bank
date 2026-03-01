using AtmMachine.WebUI.Banking.Models;
using AtmMachine.WebUI.Banking.Services;
using AtmMachine.WebUI.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AtmMachine.WebUI.Pages;

public sealed class SettingsModel : AuthenticatedPageModel
{
    private readonly BankingService _bankingService;

    public SettingsModel(BankingService bankingService)
    {
        _bankingService = bankingService;
    }

    [BindProperty]
    public string FirstName { get; set; } = string.Empty;

    [BindProperty]
    public string LastName { get; set; } = string.Empty;

    [BindProperty]
    public string Address { get; set; } = string.Empty;

    [BindProperty]
    public string Phone { get; set; } = string.Empty;

    [BindProperty]
    public bool EmailNotificationsEnabled { get; set; }

    [BindProperty]
    public bool SecurityAlertsEnabled { get; set; }

    [BindProperty]
    public bool MarketingEmailsEnabled { get; set; }

    [BindProperty(SupportsGet = true)]
    public Guid? AccountId { get; set; }

    public BankUser? ProfileUser { get; private set; }
    public IReadOnlyList<BankAccount> Accounts { get; private set; } = Array.Empty<BankAccount>();
    public IReadOnlyList<LoginRecord> LoginHistory { get; private set; } = Array.Empty<LoginRecord>();
    public IReadOnlyList<DeviceRecord> Devices { get; private set; } = Array.Empty<DeviceRecord>();

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

        if (!await LoadPageDataAsync(userId))
        {
            return RedirectToLoginPage();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostProfileAsync()
    {
        if (!TryGetAuthenticatedUser(out Guid userId))
        {
            return RedirectToLoginPage();
        }

        OperationResult result = await _bankingService.SaveProfileAsync(
            userId,
            FirstName,
            LastName,
            Address,
            Phone);

        if (result.IsSuccess)
        {
            SuccessMessage = result.Message;
        }
        else
        {
            ErrorMessage = result.Message;
        }

        return RedirectToPage(new { accountId = AccountId });
    }

    public async Task<IActionResult> OnPostPreferencesAsync()
    {
        if (!TryGetAuthenticatedUser(out Guid userId))
        {
            return RedirectToLoginPage();
        }

        OperationResult result = await _bankingService.UpdateSettingsAsync(
            userId,
            EmailNotificationsEnabled,
            SecurityAlertsEnabled,
            MarketingEmailsEnabled);

        if (result.IsSuccess)
        {
            SuccessMessage = result.Message;
        }
        else
        {
            ErrorMessage = result.Message;
        }

        return RedirectToPage(new { accountId = AccountId });
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

    private async Task<bool> LoadPageDataAsync(Guid userId)
    {
        ProfileUser = await _bankingService.GetUserAsync(userId);
        if (ProfileUser is null)
        {
            return false;
        }

        Accounts = await _bankingService.GetAccountsAsync(userId);
        LoginHistory = await _bankingService.GetLoginHistoryAsync(userId);
        Devices = await _bankingService.GetDevicesAsync(userId);

        FirstName = ProfileUser.FirstName;
        LastName = ProfileUser.LastName;
        Address = ProfileUser.Address;
        Phone = ProfileUser.Phone;
        EmailNotificationsEnabled = ProfileUser.Settings.EmailNotificationsEnabled;
        SecurityAlertsEnabled = ProfileUser.Settings.SecurityAlertsEnabled;
        MarketingEmailsEnabled = ProfileUser.Settings.MarketingEmailsEnabled;

        return true;
    }
}
