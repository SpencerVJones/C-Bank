using AtmMachine.WebUI.Banking.Models;
using AtmMachine.WebUI.Banking.Services;
using AtmMachine.WebUI.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AtmMachine.WebUI.Pages;

public sealed class DisputesModel : AuthenticatedPageModel
{
    private readonly BankingService _bankingService;

    public DisputesModel(BankingService bankingService)
    {
        _bankingService = bankingService;
    }

    public IReadOnlyList<DisputeTicket> Disputes { get; private set; } = Array.Empty<DisputeTicket>();

    public async Task<IActionResult> OnGetAsync()
    {
        if (!TryGetAuthenticatedUser(out Guid userId))
        {
            return RedirectToLoginPage();
        }

        Disputes = await _bankingService.GetDisputesAsync(userId);
        return Page();
    }
}
