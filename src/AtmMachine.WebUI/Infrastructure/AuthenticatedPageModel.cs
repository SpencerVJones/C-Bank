using AtmMachine.WebUI.Banking.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AtmMachine.WebUI.Infrastructure;

public abstract class AuthenticatedPageModel : PageModel
{
    protected bool TryGetAuthenticatedUser(out Guid userId)
    {
        Guid? sessionUserId = HttpContext.Session.GetCurrentUserId();
        if (!sessionUserId.HasValue)
        {
            userId = Guid.Empty;
            return false;
        }

        userId = sessionUserId.Value;
        return true;
    }

    protected bool TryGetAuthenticatedUser(out Guid userId, out UserRole role)
    {
        Guid? sessionUserId = HttpContext.Session.GetCurrentUserId();
        UserRole? sessionRole = HttpContext.Session.GetCurrentUserRole();
        if (!sessionUserId.HasValue || !sessionRole.HasValue)
        {
            userId = Guid.Empty;
            role = UserRole.User;
            return false;
        }

        userId = sessionUserId.Value;
        role = sessionRole.Value;
        return true;
    }

    protected IActionResult RedirectToLoginPage() => RedirectToPage("/Login");

    protected IActionResult RedirectToDashboardPage() => RedirectToPage("/Dashboard");
}
