using AtmMachine.WebUI.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AtmMachine.WebUI.Pages;

public sealed class IndexModel : AuthenticatedPageModel
{
    public IActionResult OnGet()
    {
        if (!TryGetAuthenticatedUser(out _))
        {
            return RedirectToLoginPage();
        }

        return RedirectToDashboardPage();
    }
}
