using System.Text;
using System.Text.Json;

namespace AtmMachine.WebUI.Banking.Services;

public sealed class FirebaseAuthClient
{
    private readonly HttpClient _httpClient;
    private readonly FirebaseAuthOptions _options;

    public FirebaseAuthClient(HttpClient httpClient, FirebaseAuthOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public bool Enabled => _options.Enabled && !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async Task<FirebaseAuthResult> SignUpAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return FirebaseAuthResult.Fail("Firebase auth provider is disabled.");
        }

        return await SendAsync("accounts:signUp", email, password, cancellationToken);
    }

    public async Task<FirebaseAuthResult> SignInAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        if (!Enabled)
        {
            return FirebaseAuthResult.Fail("Firebase auth provider is disabled.");
        }

        return await SendAsync("accounts:signInWithPassword", email, password, cancellationToken);
    }

    private async Task<FirebaseAuthResult> SendAsync(
        string route,
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        string normalizedEmail = (email ?? string.Empty).Trim().ToLowerInvariant();
        string normalizedPassword = (password ?? string.Empty).Trim();
        string url = $"{NormalizeEndpoint(_options.Endpoint)}{route}?key={Uri.EscapeDataString(_options.ApiKey)}";

        string payload = JsonSerializer.Serialize(new
        {
            email = normalizedEmail,
            password = normalizedPassword,
            returnSecureToken = true
        });

        using HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            using JsonDocument document = JsonDocument.Parse(responseBody);
            JsonElement root = document.RootElement;
            string? localId = root.TryGetProperty("localId", out JsonElement localIdElement)
                ? localIdElement.GetString()
                : null;
            string? idToken = root.TryGetProperty("idToken", out JsonElement idTokenElement)
                ? idTokenElement.GetString()
                : null;

            return FirebaseAuthResult.Ok(localId, idToken);
        }

        string error = TryExtractError(responseBody) ?? $"Firebase auth request failed: {(int)response.StatusCode}";
        return FirebaseAuthResult.Fail(error);
    }

    private static string NormalizeEndpoint(string endpoint)
    {
        string normalized = string.IsNullOrWhiteSpace(endpoint)
            ? "https://identitytoolkit.googleapis.com/v1/"
            : endpoint.Trim();

        if (!normalized.EndsWith('/'))
        {
            normalized += "/";
        }

        if (!normalized.Contains("/v1/", StringComparison.OrdinalIgnoreCase))
        {
            normalized += "v1/";
        }

        return normalized;
    }

    private static string? TryExtractError(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("error", out JsonElement errorNode))
            {
                if (errorNode.TryGetProperty("message", out JsonElement messageNode))
                {
                    return messageNode.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Ignore parse errors and fallback to generic status.
        }

        return null;
    }
}

public sealed record FirebaseAuthOptions(
    bool Enabled,
    string ApiKey,
    string Endpoint);

public sealed record FirebaseAuthResult(
    bool IsSuccess,
    string Message,
    string? LocalId,
    string? IdToken)
{
    public static FirebaseAuthResult Ok(string? localId, string? idToken) =>
        new(true, "Firebase auth request succeeded.", localId, idToken);

    public static FirebaseAuthResult Fail(string message) =>
        new(false, message, null, null);
}
