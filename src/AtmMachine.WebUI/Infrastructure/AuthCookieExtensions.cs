using AtmMachine.WebUI.Banking.Services;

namespace AtmMachine.WebUI.Infrastructure;

public static class AuthCookieExtensions
{
    public static void AppendAuthCookies(
        this HttpResponse response,
        string accessToken,
        string? refreshToken,
        DateTimeOffset nowUtc)
    {
        response.Cookies.Append(AuthCookieNames.AccessToken, accessToken, new CookieOptions
        {
            HttpOnly = true,
            IsEssential = true,
            Path = "/",
            Secure = response.HttpContext.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Expires = nowUtc.Add(BankingSecurityService.AccessTokenLifetime)
        });

        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            response.Cookies.Append(AuthCookieNames.RefreshToken, refreshToken, new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                Path = "/",
                Secure = response.HttpContext.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Expires = nowUtc.Add(BankingSecurityService.RefreshTokenLifetime)
            });
        }
    }

    public static void ClearAuthCookies(this HttpResponse response)
    {
        CookieOptions options = new()
        {
            Path = "/"
        };

        response.Cookies.Delete(AuthCookieNames.AccessToken, options);
        response.Cookies.Delete(AuthCookieNames.RefreshToken, options);
    }
}
