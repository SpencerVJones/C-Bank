using AtmMachine.WebUI.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AtmMachine.WebUI.Pages;

public sealed class LogoutModel : AuthenticatedPageModel
{
    public IActionResult OnGet()
    {
        return SignOutAndRedirect();
    }

    public IActionResult OnPost()
    {
        return SignOutAndRedirect();
    }

    private IActionResult SignOutAndRedirect()
    {
        HttpContext.Session.ClearAuthentication();
        Response.ClearAuthCookies();
        return RedirectToLoginPage();
    }
}
