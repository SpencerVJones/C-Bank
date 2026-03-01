using AtmMachine.WebUI.Banking.Models;
using AtmMachine.WebUI.Banking.Services;
using AtmMachine.WebUI.Infrastructure;
using Microsoft.AspNetCore.SignalR;

namespace AtmMachine.WebUI.Realtime;

public sealed class BankingHub : Hub
{
    public const string AdminGroupName = "admins";

    private readonly BankingService _bankingService;

    public BankingHub(BankingService bankingService)
    {
        _bankingService = bankingService;
    }

    public override async Task OnConnectedAsync()
    {
        if (!TryResolveIdentity(out Guid userId, out UserRole role))
        {
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId));
        if (role == UserRole.Admin)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, AdminGroupName);
        }

        await Clients.Caller.SendAsync("session.ready", new
        {
            userId = userId.ToString("N"),
            role = role.ToString()
        });

        await base.OnConnectedAsync();
    }

    public static string UserGroup(Guid userId) => $"user:{userId:N}";

    private bool TryResolveIdentity(out Guid userId, out UserRole role)
    {
        userId = Guid.Empty;
        role = UserRole.User;

        HttpContext? httpContext = Context.GetHttpContext();
        if (httpContext is null)
        {
            return false;
        }

        Guid? sessionUserId = httpContext.Session.GetCurrentUserId();
        UserRole? sessionRole = httpContext.Session.GetCurrentUserRole();
        if (sessionUserId.HasValue && sessionRole.HasValue)
        {
            userId = sessionUserId.Value;
            role = sessionRole.Value;
            return true;
        }

        string? token = httpContext.Request.Cookies[AuthCookieNames.AccessToken];
        if (!string.IsNullOrWhiteSpace(token) &&
            _bankingService.TryValidateAccessToken(token, out TokenPayload payload))
        {
            userId = payload.UserId;
            role = payload.Role;
            return true;
        }

        return false;
    }
}
