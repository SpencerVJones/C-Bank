using AtmMachine.WebUI.Banking.Services;
using AtmMachine.WebUI.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AtmMachine.WebUI.Pages;

public sealed class SignupModel : AuthenticatedPageModel
{
    private readonly BankingService _bankingService;

    public SignupModel(BankingService bankingService)
    {
        _bankingService = bankingService;
    }

    [BindProperty]
    public string FirstName { get; set; } = string.Empty;

    [BindProperty]
    public string LastName { get; set; } = string.Empty;

    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    public string Address { get; set; } = string.Empty;

    [BindProperty]
    public string Phone { get; set; } = string.Empty;

    public string? Message { get; private set; }
    public bool IsSuccess { get; private set; }

    public IActionResult OnGet()
    {
        if (TryGetAuthenticatedUser(out _))
        {
            return RedirectToDashboardPage();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        OperationResult result = await _bankingService.SignupAsync(new SignupRequest(
            Email,
            Password,
            FirstName,
            LastName,
            Address,
            Phone));

        IsSuccess = result.IsSuccess;
        Message = result.Message;

        if (result.IsSuccess)
        {
            TempData["signup_success"] = result.Message;
            return RedirectToLoginPage();
        }

        return Page();
    }
}
