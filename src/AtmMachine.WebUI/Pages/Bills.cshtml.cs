using AtmMachine.WebUI.Banking.Models;
using AtmMachine.WebUI.Banking.Services;
using AtmMachine.WebUI.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AtmMachine.WebUI.Pages;

public sealed class BillsModel : AuthenticatedPageModel
{
    private readonly BankingService _bankingService;

    public BillsModel(BankingService bankingService)
    {
        _bankingService = bankingService;
    }

    public DashboardSnapshot? Snapshot { get; private set; }
    public IReadOnlyList<BankTransfer> UpcomingBillTransfers { get; private set; } = Array.Empty<BankTransfer>();

    public async Task<IActionResult> OnGetAsync()
    {
        if (!TryGetAuthenticatedUser(out Guid userId))
        {
            return RedirectToLoginPage();
        }

        Snapshot = await _bankingService.GetDashboardSnapshotAsync(userId, 45);
        if (Snapshot is null)
        {
            return RedirectToLoginPage();
        }

        UpcomingBillTransfers = Snapshot.UpcomingTransfers
            .Where(transfer => transfer.IsExternalAch)
            .OrderBy(transfer => transfer.NextRunUtc ?? transfer.ScheduledUtc)
            .ToList();

        return Page();
    }
}
