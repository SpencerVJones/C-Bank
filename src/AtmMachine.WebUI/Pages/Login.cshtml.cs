using AtmMachine.WebUI.Banking.Services;
using AtmMachine.WebUI.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AtmMachine.WebUI.Pages;

[EnableRateLimiting("auth")]
public sealed class LoginModel : AuthenticatedPageModel
{
    private readonly BankingService _bankingService;

    public LoginModel(BankingService bankingService)
    {
        _bankingService = bankingService;
    }

    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string Password { get; set; } = string.Empty;

    public string? ErrorMessage { get; private set; }
    public string? SuccessMessage { get; private set; }

    public IActionResult OnGet()
    {
        if (TryGetAuthenticatedUser(out _))
        {
            return RedirectToDashboardPage();
        }

        SuccessMessage = TempData["signup_success"]?.ToString();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        string deviceId = HttpContext.Session.GetOrCreateDeviceId();
        string deviceName = Request.Headers.UserAgent.ToString().Split(' ').FirstOrDefault() ?? "Browser";
        string ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";

        LoginCompleteResult result = await _bankingService.LoginAsync(
            Email,
            Password,
            deviceId,
            deviceName,
            ipAddress,
            Request.Headers.UserAgent.ToString());

        if (!result.IsSuccess || !result.UserId.HasValue || string.IsNullOrWhiteSpace(result.AccessToken))
        {
            ErrorMessage = result.Message;
            return Page();
        }

        HttpContext.Session.SetAuthenticatedUser(result.UserId.Value, result.Role);
        Response.AppendAuthCookies(result.AccessToken, result.RefreshToken, DateTimeOffset.UtcNow);

        return RedirectToDashboardPage();
    }
}
