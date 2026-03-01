using System.Text;
using System.Text.Json;
using AtmMachine.WebUI.Banking.Models;

namespace AtmMachine.WebUI.Banking.Infrastructure;

public sealed class FirebaseBankingDataStore : IBankingDataStore
{
    private readonly HttpClient _httpClient;
    private readonly FirebaseBankingStoreOptions _options;
    private readonly string _serviceEmail;
    private readonly string _servicePassword;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public FirebaseBankingDataStore(HttpClient httpClient, FirebaseBankingStoreOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new ArgumentException("Firebase ApiKey is required for FirebaseBankingDataStore.");
        }
        if (string.IsNullOrWhiteSpace(_options.ProjectId))
        {
            throw new ArgumentException("Firebase ProjectId is required for FirebaseBankingDataStore.");
        }
        if (string.IsNullOrWhiteSpace(_options.CollectionId))
        {
            throw new ArgumentException("Firebase CollectionId is required for FirebaseBankingDataStore.");
        }
        if (string.IsNullOrWhiteSpace(_options.DocumentId))
        {
            throw new ArgumentException("Firebase DocumentId is required for FirebaseBankingDataStore.");
        }

        _serviceEmail = _options.ServiceEmail?.Trim() ?? string.Empty;
        _servicePassword = _options.ServicePassword?.Trim() ?? string.Empty;
    }

    public async Task<T> ReadAsync<T>(
        Func<BankDatabase, T> query,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            BankDatabase database = await LoadUnsafeAsync(cancellationToken);
            return query(database);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<T> WriteAsync<T>(
        Func<BankDatabase, T> mutate,
        CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            BankDatabase database = await LoadUnsafeAsync(cancellationToken);
            T result = mutate(database);
            await SaveUnsafeAsync(database, cancellationToken);
            return result;
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task WriteAsync(
        Action<BankDatabase> mutate,
        CancellationToken cancellationToken = default)
    {
        await WriteAsync(database =>
        {
            mutate(database);
            return true;
        }, cancellationToken);
    }

    private async Task<BankDatabase> LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, BuildDocumentUrl());
        await MaybeAttachAuthAsync(request, cancellationToken);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

        if ((int)response.StatusCode == 404)
        {
            return new BankDatabase();
        }

        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Firebase Firestore read failed ({(int)response.StatusCode}): {responseBody}");
        }

        string? payload = TryGetStringField(responseBody, "payload");
        if (string.IsNullOrWhiteSpace(payload))
        {
            return new BankDatabase();
        }

        BankDatabase? database = JsonSerializer.Deserialize<BankDatabase>(payload, _jsonOptions);
        return database ?? new BankDatabase();
    }

    private async Task SaveUnsafeAsync(BankDatabase database, CancellationToken cancellationToken)
    {
        string payload = JsonSerializer.Serialize(database, _jsonOptions);
        string updatedUtc = DateTimeOffset.UtcNow.ToString("O");

        string requestJson = JsonSerializer.Serialize(new
        {
            fields = new
            {
                payload = new { stringValue = payload },
                updatedUtc = new { stringValue = updatedUtc }
            }
        });

        string patchUrl = $"{BuildDocumentUrl()}&updateMask.fieldPaths=payload&updateMask.fieldPaths=updatedUtc";
        using HttpRequestMessage request = new(HttpMethod.Patch, patchUrl)
        {
            Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
        };
        await MaybeAttachAuthAsync(request, cancellationToken);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Firebase Firestore write failed ({(int)response.StatusCode}): {responseBody}");
        }
    }

    private string BuildDocumentUrl()
    {
        string projectId = Uri.EscapeDataString(_options.ProjectId);
        string collection = Uri.EscapeDataString(_options.CollectionId);
        string document = Uri.EscapeDataString(_options.DocumentId);
        string key = Uri.EscapeDataString(_options.ApiKey);

        return $"{NormalizeFirestoreEndpoint(_options.FirestoreEndpoint)}projects/{projectId}/databases/(default)/documents/{collection}/{document}?key={key}";
    }

    private async Task MaybeAttachAuthAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_serviceEmail) || string.IsNullOrWhiteSpace(_servicePassword))
        {
            return;
        }

        string token = await GetServiceIdTokenAsync(cancellationToken);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
    }

    private async Task<string> GetServiceIdTokenAsync(CancellationToken cancellationToken)
    {
        string url = $"{NormalizeAuthEndpoint(_options.AuthEndpoint)}accounts:signInWithPassword?key={Uri.EscapeDataString(_options.ApiKey)}";
        string body = JsonSerializer.Serialize(new
        {
            email = _serviceEmail,
            password = _servicePassword,
            returnSecureToken = true
        });

        using HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Firebase auth token request failed ({(int)response.StatusCode}): {responseBody}");
        }

        using JsonDocument document = JsonDocument.Parse(responseBody);
        if (document.RootElement.TryGetProperty("idToken", out JsonElement tokenNode))
        {
            string? token = tokenNode.GetString();
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }
        }

        throw new InvalidOperationException("Firebase auth token response did not contain idToken.");
    }

    private static string NormalizeFirestoreEndpoint(string endpoint)
    {
        string normalized = string.IsNullOrWhiteSpace(endpoint)
            ? "https://firestore.googleapis.com/v1/"
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

    private static string NormalizeAuthEndpoint(string endpoint)
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

    private static string? TryGetStringField(string documentJson, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(documentJson))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(documentJson);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("fields", out JsonElement fieldsNode))
            {
                return null;
            }

            if (!fieldsNode.TryGetProperty(fieldName, out JsonElement fieldNode))
            {
                return null;
            }

            if (!fieldNode.TryGetProperty("stringValue", out JsonElement valueNode))
            {
                return null;
            }

            return valueNode.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record FirebaseBankingStoreOptions(
    string ProjectId,
    string ApiKey,
    string CollectionId,
    string DocumentId = "bank_state",
    string FirestoreEndpoint = "https://firestore.googleapis.com/v1",
    string AuthEndpoint = "https://identitytoolkit.googleapis.com/v1",
    string? ServiceEmail = null,
    string? ServicePassword = null);
