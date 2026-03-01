using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AtmMachine.WebUI.Banking.Models;

namespace AtmMachine.WebUI.Banking.Infrastructure;

public sealed class AppwriteBankingDataStore : IBankingDataStore
{
    private readonly HttpClient _httpClient;
    private readonly string _databaseId;
    private readonly string _collectionId;
    private readonly string _documentId;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public AppwriteBankingDataStore(HttpClient httpClient, AppwriteBankingStoreOptions options)
    {
        if (httpClient is null)
        {
            throw new ArgumentNullException(nameof(httpClient));
        }

        _httpClient = httpClient;
        _databaseId = Require(options.DatabaseId, nameof(options.DatabaseId));
        _collectionId = Require(options.CollectionId, nameof(options.CollectionId));
        _documentId = Require(options.DocumentId, nameof(options.DocumentId));

        string endpoint = NormalizeEndpoint(Require(options.Endpoint, nameof(options.Endpoint)));
        _httpClient.BaseAddress = new Uri(endpoint, UriKind.Absolute);

        _httpClient.DefaultRequestHeaders.Remove("X-Appwrite-Project");
        _httpClient.DefaultRequestHeaders.Remove("X-Appwrite-Key");
        _httpClient.DefaultRequestHeaders.Add("X-Appwrite-Project", Require(options.ProjectId, nameof(options.ProjectId)));
        _httpClient.DefaultRequestHeaders.Add("X-Appwrite-Key", Require(options.ApiKey, nameof(options.ApiKey)));
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (_httpClient.Timeout == Timeout.InfiniteTimeSpan)
        {
            _httpClient.Timeout = TimeSpan.FromSeconds(45);
        }
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
        using HttpRequestMessage request = new(HttpMethod.Get, DocumentPath);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new BankDatabase();
        }

        string payloadJson = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Appwrite read failed ({(int)response.StatusCode}): {payloadJson}");
        }

        string? payload = TryExtractPayload(payloadJson);
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

        string patchBody = JsonSerializer.Serialize(new
        {
            data = new
            {
                payload,
                updatedUtc
            }
        });

        using HttpRequestMessage patchRequest = new(HttpMethod.Patch, DocumentPath)
        {
            Content = new StringContent(patchBody, Encoding.UTF8, "application/json")
        };
        using HttpResponseMessage patchResponse = await _httpClient.SendAsync(patchRequest, cancellationToken);

        if (patchResponse.IsSuccessStatusCode)
        {
            return;
        }

        if (patchResponse.StatusCode != HttpStatusCode.NotFound)
        {
            string patchError = await patchResponse.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Appwrite update failed ({(int)patchResponse.StatusCode}): {patchError}");
        }

        string createBody = JsonSerializer.Serialize(new
        {
            documentId = _documentId,
            data = new
            {
                payload,
                updatedUtc
            }
        });

        using HttpRequestMessage createRequest = new(HttpMethod.Post, DocumentsPath)
        {
            Content = new StringContent(createBody, Encoding.UTF8, "application/json")
        };
        using HttpResponseMessage createResponse = await _httpClient.SendAsync(createRequest, cancellationToken);

        if (createResponse.IsSuccessStatusCode)
        {
            return;
        }

        if (createResponse.StatusCode == HttpStatusCode.Conflict)
        {
            using HttpRequestMessage retryPatch = new(HttpMethod.Patch, DocumentPath)
            {
                Content = new StringContent(patchBody, Encoding.UTF8, "application/json")
            };
            using HttpResponseMessage retryPatchResponse = await _httpClient.SendAsync(retryPatch, cancellationToken);
            if (retryPatchResponse.IsSuccessStatusCode)
            {
                return;
            }

            string retryPatchError = await retryPatchResponse.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Appwrite retry update failed ({(int)retryPatchResponse.StatusCode}): {retryPatchError}");
        }

        string createError = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"Appwrite create failed ({(int)createResponse.StatusCode}): {createError}");
    }

    private string DocumentPath => $"databases/{_databaseId}/collections/{_collectionId}/documents/{_documentId}";

    private string DocumentsPath => $"databases/{_databaseId}/collections/{_collectionId}/documents";

    private static string? TryExtractPayload(string documentJson)
    {
        using JsonDocument document = JsonDocument.Parse(documentJson);
        JsonElement root = document.RootElement;

        if (root.TryGetProperty("payload", out JsonElement payloadAtRoot) &&
            payloadAtRoot.ValueKind == JsonValueKind.String)
        {
            return payloadAtRoot.GetString();
        }

        if (root.TryGetProperty("data", out JsonElement dataNode) &&
            dataNode.ValueKind == JsonValueKind.Object &&
            dataNode.TryGetProperty("payload", out JsonElement payloadInData) &&
            payloadInData.ValueKind == JsonValueKind.String)
        {
            return payloadInData.GetString();
        }

        return null;
    }

    private static string NormalizeEndpoint(string endpoint)
    {
        string normalized = endpoint.Trim().TrimEnd('/');
        if (!normalized.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            normalized += "/v1";
        }

        return $"{normalized}/";
    }

    private static string Require(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Appwrite setting '{label}' is required.");
        }

        return value.Trim();
    }
}

public sealed record AppwriteBankingStoreOptions(
    string Endpoint,
    string ProjectId,
    string ApiKey,
    string DatabaseId,
    string CollectionId,
    string DocumentId = "bank_state");
