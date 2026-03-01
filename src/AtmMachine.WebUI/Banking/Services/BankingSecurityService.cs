using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AtmMachine.WebUI.Banking.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AtmMachine.WebUI.Banking.Services;

public sealed class BankingSecurityService
{
    public const int MinimumPasswordLength = 8;
    public const int PasswordHashIterations = 120_000;
    public const int LockoutThreshold = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(20);
    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(30);

    private readonly byte[] _jwtSecretKey;
    private readonly ILogger<BankingSecurityService> _logger;

    public BankingSecurityService(
        IConfiguration configuration,
        ILogger<BankingSecurityService> logger)
    {
        _logger = logger;

        string? configuredKey = configuration["Banking:Security:JwtSigningKey"];
        if (string.IsNullOrWhiteSpace(configuredKey))
        {
            _jwtSecretKey = RandomNumberGenerator.GetBytes(32);
            _logger.LogWarning(
                "Banking:Security:JwtSigningKey is not configured. Using an ephemeral signing key; access tokens will be invalid after process restart.");
            return;
        }

        _jwtSecretKey = ResolveJwtSecretKey(configuredKey.Trim());
    }

    public (string Hash, string Salt) HashPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinimumPasswordLength)
        {
            throw new ArgumentException($"Password must be at least {MinimumPasswordLength} characters.", nameof(password));
        }

        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            PasswordHashIterations,
            HashAlgorithmName.SHA256,
            32);

        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public bool VerifyPassword(string password, string hash, string salt)
    {
        if (string.IsNullOrWhiteSpace(password) ||
            string.IsNullOrWhiteSpace(hash) ||
            string.IsNullOrWhiteSpace(salt))
        {
            return false;
        }

        try
        {
            byte[] saltBytes = Convert.FromBase64String(salt);
            byte[] expectedHash = Convert.FromBase64String(hash);
            byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(
                password,
                saltBytes,
                PasswordHashIterations,
                HashAlgorithmName.SHA256,
                expectedHash.Length);

            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public string IssueAccessToken(Guid userId, UserRole role, DateTimeOffset issuedUtc, TimeSpan lifetime)
    {
        var header = new Dictionary<string, object?>
        {
            ["alg"] = "HS256",
            ["typ"] = "JWT"
        };
        var payload = new Dictionary<string, object?>
        {
            ["sub"] = userId.ToString("N"),
            ["role"] = role.ToString(),
            ["iat"] = ToUnixSeconds(issuedUtc),
            ["exp"] = ToUnixSeconds(issuedUtc.Add(lifetime))
        };

        string encodedHeader = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header));
        string encodedPayload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
        string unsigned = $"{encodedHeader}.{encodedPayload}";
        string signature = Base64UrlEncode(Sign(unsigned));

        return $"{unsigned}.{signature}";
    }

    public bool TryValidateAccessToken(string token, DateTimeOffset nowUtc, out TokenPayload payload)
    {
        payload = new TokenPayload(Guid.Empty, UserRole.User, DateTimeOffset.MinValue);

        string[] segments = token.Split('.');
        if (segments.Length != 3)
        {
            return false;
        }

        string unsigned = $"{segments[0]}.{segments[1]}";
        byte[] expectedSignature = Sign(unsigned);
        byte[] providedSignature;

        try
        {
            providedSignature = Base64UrlDecode(segments[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, providedSignature))
        {
            return false;
        }

        try
        {
            byte[] payloadBytes = Base64UrlDecode(segments[1]);
            using JsonDocument document = JsonDocument.Parse(payloadBytes);
            string? sub = document.RootElement.GetProperty("sub").GetString();
            string? roleText = document.RootElement.GetProperty("role").GetString();
            long exp = document.RootElement.GetProperty("exp").GetInt64();

            if (!Guid.TryParseExact(sub, "N", out Guid userId))
            {
                return false;
            }

            if (!Enum.TryParse<UserRole>(roleText, ignoreCase: true, out UserRole role))
            {
                return false;
            }

            DateTimeOffset expiresUtc = DateTimeOffset.FromUnixTimeSeconds(exp);
            if (expiresUtc <= nowUtc)
            {
                return false;
            }

            payload = new TokenPayload(userId, role, expiresUtc);
            return true;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public string GenerateRefreshToken()
    {
        return Base64UrlEncode(RandomNumberGenerator.GetBytes(48));
    }

    private byte[] Sign(string value)
    {
        using HMACSHA256 hmac = new(_jwtSecretKey);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
    }

    private static byte[] ResolveJwtSecretKey(string configuredKey)
    {
        if (TryDecodeBase64(configuredKey, out byte[] decodedBytes) && decodedBytes.Length >= 32)
        {
            return decodedBytes;
        }

        byte[] utf8Bytes = Encoding.UTF8.GetBytes(configuredKey);
        if (utf8Bytes.Length < 32)
        {
            throw new InvalidOperationException(
                "Banking:Security:JwtSigningKey must be at least 32 bytes when provided as plain text, or at least 32 decoded bytes when provided as base64.");
        }

        return utf8Bytes;
    }

    private static bool TryDecodeBase64(string value, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(value);
            return true;
        }
        catch (FormatException)
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }

    private static long ToUnixSeconds(DateTimeOffset value) => value.ToUnixTimeSeconds();

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string value)
    {
        string padded = value
            .Replace('-', '+')
            .Replace('_', '/');

        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
            case 0:
                break;
            default:
                throw new FormatException("Invalid base64url string.");
        }

        return Convert.FromBase64String(padded);
    }
}

public readonly record struct TokenPayload(Guid UserId, UserRole Role, DateTimeOffset ExpiresUtc);
