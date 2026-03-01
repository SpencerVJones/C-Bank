using AtmMachine.WebUI.Banking.Models;

namespace AtmMachine.WebUI.Infrastructure;

public static class AuthSessionExtensions
{
    public static Guid? GetCurrentUserId(this ISession session)
    {
        string? raw = session.GetString(SessionKeys.CurrentUserId);
        return Guid.TryParse(raw, out Guid parsed) ? parsed : null;
    }

    public static UserRole? GetCurrentUserRole(this ISession session)
    {
        string? raw = session.GetString(SessionKeys.CurrentUserRole);
        return Enum.TryParse<UserRole>(raw, ignoreCase: true, out UserRole parsed) ? parsed : null;
    }

    public static void SetAuthenticatedUser(this ISession session, Guid userId, UserRole role)
    {
        session.SetString(SessionKeys.CurrentUserId, userId.ToString("N"));
        session.SetString(SessionKeys.CurrentUserRole, role.ToString());
    }

    public static void ClearAuthentication(this ISession session)
    {
        session.Remove(SessionKeys.CurrentUserId);
        session.Remove(SessionKeys.CurrentUserRole);
    }

    public static string GetOrCreateDeviceId(this ISession session)
    {
        string? existing = session.GetString(SessionKeys.DeviceId);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        string deviceId = Guid.NewGuid().ToString("N");
        session.SetString(SessionKeys.DeviceId, deviceId);
        return deviceId;
    }
}
