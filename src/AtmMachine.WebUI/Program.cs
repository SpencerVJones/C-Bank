using AtmMachine.WebUI.Banking.Infrastructure;
using AtmMachine.WebUI.Banking.Models;
using AtmMachine.WebUI.Banking.Services;
using AtmMachine.WebUI.Infrastructure;
using AtmMachine.WebUI.Observability;
using AtmMachine.WebUI.Realtime;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Npgsql;
using System.Globalization;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.Configure(options =>
{
    options.ActivityTrackingOptions =
        ActivityTrackingOptions.SpanId |
        ActivityTrackingOptions.TraceId |
        ActivityTrackingOptions.ParentId;
});
builder.Logging.AddJsonConsole(options =>
{
    options.IncludeScopes = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
    options.UseUtcTimestamp = true;
    options.JsonWriterOptions = new System.Text.Json.JsonWriterOptions
    {
        Indented = false
    };
});

builder.Services.AddRazorPages();
builder.Services.AddSignalR();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSingleton<BankingTelemetry>();
builder.Services.AddSingleton<BankingRealtimeQueue>();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddSession(options =>
{
    options.Cookie.Name = "consoleatm.session";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.IdleTimeout = TimeSpan.FromMinutes(40);
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("auth", limiter =>
    {
        limiter.PermitLimit = 6;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
    options.AddTokenBucketLimiter("transfers", limiter =>
    {
        limiter.TokenLimit = 10;
        limiter.TokensPerPeriod = 10;
        limiter.ReplenishmentPeriod = TimeSpan.FromMinutes(1);
        limiter.AutoReplenishment = true;
        limiter.QueueLimit = 0;
        limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
});

builder.Services.AddSingleton<FirebaseAuthClient>(services =>
{
    IConfiguration configuration = services.GetRequiredService<IConfiguration>();
    IHttpClientFactory httpClientFactory = services.GetRequiredService<IHttpClientFactory>();

    FirebaseAuthOptions options = new(
        Enabled: GetBool(configuration, "Banking:Firebase:Enabled", defaultValue: false),
        ApiKey: configuration["Banking:Firebase:ApiKey"] ?? string.Empty,
        Endpoint: configuration["Banking:Firebase:AuthEndpoint"] ?? "https://identitytoolkit.googleapis.com/v1");

    return new FirebaseAuthClient(
        httpClientFactory.CreateClient(nameof(FirebaseAuthClient)),
        options);
});
builder.Services.AddSingleton<IBankingDataStore>(services =>
{
    IConfiguration configuration = services.GetRequiredService<IConfiguration>();
    IWebHostEnvironment environment = services.GetRequiredService<IWebHostEnvironment>();
    IHttpClientFactory httpClientFactory = services.GetRequiredService<IHttpClientFactory>();

    string provider = (configuration["Banking:Provider"] ?? "sqlite")
        .Trim()
        .ToLowerInvariant();

    string jsonPath = Path.GetFullPath(Path.Combine(
        environment.ContentRootPath,
        "..",
        "..",
        "data",
        "banking.json"));

    string sqlitePath = Path.GetFullPath(Path.Combine(
        environment.ContentRootPath,
        "..",
        "..",
        "data",
        "banking.sqlite"));

    string sqliteExecutable = configuration["Banking:SqliteExecutable"] ?? "sqlite3";

    return provider switch
    {
        "json" => new BankingDataStore(jsonPath),
        "sqlite" => new SqliteBankingDataStore(sqlitePath, sqliteExecutable),
        "firebase" => new FirebaseBankingDataStore(
            httpClientFactory.CreateClient(nameof(FirebaseBankingDataStore)),
            new FirebaseBankingStoreOptions(
                ProjectId: configuration["Banking:Firebase:ProjectId"] ?? string.Empty,
                ApiKey: configuration["Banking:Firebase:ApiKey"] ?? string.Empty,
                CollectionId: configuration["Banking:Firebase:CollectionId"] ?? "banking_state",
                DocumentId: configuration["Banking:Firebase:DocumentId"] ?? "bank_state",
                FirestoreEndpoint: configuration["Banking:Firebase:FirestoreEndpoint"] ?? "https://firestore.googleapis.com/v1",
                AuthEndpoint: configuration["Banking:Firebase:AuthEndpoint"] ?? "https://identitytoolkit.googleapis.com/v1",
                ServiceEmail: configuration["Banking:Firebase:ServiceEmail"],
                ServicePassword: configuration["Banking:Firebase:ServicePassword"])),
        "appwrite" => new AppwriteBankingDataStore(
            httpClientFactory.CreateClient(nameof(AppwriteBankingDataStore)),
            new AppwriteBankingStoreOptions(
                Endpoint: GetRequired(configuration, "Banking:Appwrite:Endpoint"),
                ProjectId: GetRequired(configuration, "Banking:Appwrite:ProjectId"),
                ApiKey: GetRequired(configuration, "Banking:Appwrite:ApiKey"),
                DatabaseId: GetRequired(configuration, "Banking:Appwrite:DatabaseId"),
                CollectionId: GetRequired(configuration, "Banking:Appwrite:CollectionId"),
                DocumentId: configuration["Banking:Appwrite:DocumentId"] ?? "bank_state")),
        "postgres" or "neon" => new PostgresBankingDataStore(
            new PostgresBankingStoreOptions(
                ConnectionString: ResolvePostgresConnectionString(configuration),
                StateKey: configuration["Banking:Postgres:StateKey"] ?? "bank_state")),
        _ => throw new InvalidOperationException($"Unsupported banking data provider '{provider}'. Expected 'sqlite', 'json', 'firebase', 'appwrite', 'postgres', or 'neon'.")
    };
});
builder.Services.AddSingleton<BankingSecurityService>();
builder.Services.AddSingleton<BankingService>();
builder.Services.AddHostedService<BankingSettlementWorker>();
builder.Services.AddHostedService<BankingRealtimeDispatchWorker>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseForwardedHeaders();
app.UseHttpsRedirection();
app.UseMiddleware<RequestObservabilityMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseRateLimiter();

app.MapGet("/metrics", (BankingTelemetry telemetry) =>
    Results.Text(telemetry.BuildPrometheusSnapshot(), "text/plain; version=0.0.4"));

app.MapRazorPages();
app.MapHub<BankingHub>("/hubs/banking");
MapApiV1(app);

await SeedBankingAsync(app.Services);

app.Run();

static string GetRequired(IConfiguration configuration, string key)
{
    string? value = configuration[key];
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"Missing required setting '{key}' for Appwrite provider.");
    }

    return value.Trim();
}

static string ResolvePostgresConnectionString(IConfiguration configuration)
{
    string? raw = FirstNonEmpty(
        configuration["Banking:Postgres:ConnectionString"],
        configuration.GetConnectionString("Banking"),
        configuration.GetConnectionString("Postgres"),
        configuration["DATABASE_URL"],
        configuration["NEON_DATABASE_URL"]);

    if (string.IsNullOrWhiteSpace(raw))
    {
        throw new InvalidOperationException(
            "Missing PostgreSQL connection string. Set Banking:Postgres:ConnectionString, ConnectionStrings:Banking, ConnectionStrings:Postgres, DATABASE_URL, or NEON_DATABASE_URL.");
    }

    string normalized = raw.Trim();
    if (LooksLikePostgresUrl(normalized))
    {
        normalized = ConvertPostgresUrlToConnectionString(normalized);
    }

    NpgsqlConnectionStringBuilder builder = new(normalized);
    if (!builder.ContainsKey("SSL Mode"))
    {
        builder["SSL Mode"] = "Require";
    }

    if (!builder.ContainsKey("Trust Server Certificate"))
    {
        builder["Trust Server Certificate"] = true;
    }

    if (!builder.ContainsKey("Pooling"))
    {
        builder["Pooling"] = true;
    }

    return builder.ConnectionString;
}

static bool LooksLikePostgresUrl(string value) =>
    value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
    value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);

static string ConvertPostgresUrlToConnectionString(string value)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
    {
        return value;
    }

    string userInfo = Uri.UnescapeDataString(uri.UserInfo ?? string.Empty);
    string username = userInfo;
    string password = string.Empty;
    int separator = userInfo.IndexOf(':');
    if (separator >= 0)
    {
        username = userInfo[..separator];
        password = userInfo[(separator + 1)..];
    }

    NpgsqlConnectionStringBuilder builder = new()
    {
        Host = uri.Host,
        Port = uri.IsDefaultPort ? 5432 : uri.Port,
        Database = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/')),
        Username = username,
        Password = password
    };

    foreach ((string key, string parameterValue) in ParseQueryParameters(uri.Query))
    {
        switch (key)
        {
            case "sslmode":
            case "ssl_mode":
                builder["SSL Mode"] = parameterValue;
                break;
            case "trustservercertificate":
            case "trust_server_certificate":
                builder["Trust Server Certificate"] = parameterValue;
                break;
            case "channelbinding":
            case "channel_binding":
                builder["Channel Binding"] = parameterValue;
                break;
            case "pooling":
                builder["Pooling"] = parameterValue;
                break;
        }
    }

    return builder.ConnectionString;
}

static IEnumerable<(string Key, string Value)> ParseQueryParameters(string query)
{
    if (string.IsNullOrWhiteSpace(query))
    {
        yield break;
    }

    foreach (string segment in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
        int separator = segment.IndexOf('=');
        if (separator <= 0)
        {
            continue;
        }

        string key = Uri.UnescapeDataString(segment[..separator]).Trim().ToLowerInvariant();
        string value = Uri.UnescapeDataString(segment[(separator + 1)..]).Trim();
        if (!string.IsNullOrWhiteSpace(key))
        {
            yield return (key, value);
        }
    }
}

static string? FirstNonEmpty(params string?[] values)
{
    foreach (string? value in values)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
    }

    return null;
}

static bool GetBool(IConfiguration configuration, string key, bool defaultValue)
{
    string? raw = configuration[key];
    if (string.IsNullOrWhiteSpace(raw))
    {
        return defaultValue;
    }

    return bool.TryParse(raw, out bool parsed) ? parsed : defaultValue;
}

static async Task SeedBankingAsync(IServiceProvider services)
{
    using IServiceScope scope = services.CreateScope();
    BankingService bankingService = scope.ServiceProvider.GetRequiredService<BankingService>();
    await bankingService.SeedAsync();
}

static void MapApiV1(WebApplication app)
{
    RouteGroupBuilder api = app.MapGroup("/api/v1");

    api.MapGet("/health", () => Results.Ok(new
    {
        service = "ConsoleATM Banking API",
        version = "v1",
        timestampUtc = DateTimeOffset.UtcNow
    }));

    api.MapGet("/accounts", async (HttpContext context, BankingService bankingService) =>
    {
        if (!TryAuthenticate(context, bankingService, out Guid userId, out _))
        {
            return Results.Unauthorized();
        }

        IReadOnlyList<BankAccount> accounts = await bankingService.GetAccountsAsync(userId);
        return Results.Ok(accounts.Select(account => new
        {
            id = account.Id,
            nickname = account.Nickname,
            type = account.Type.ToString(),
            status = account.Status.ToString(),
            accountNumber = MaskAccount(account.AccountNumber),
            routingNumber = account.RoutingNumber,
            availableBalance = account.AvailableBalance,
            ledgerBalance = account.LedgerBalance
        }));
    });

    api.MapPost("/accounts", async (
        HttpContext context,
        BankingService bankingService,
        OpenAccountApiRequest request) =>
    {
        if (!TryAuthenticate(context, bankingService, out Guid userId, out _))
        {
            return Results.Unauthorized();
        }

        if (!Enum.TryParse<BankAccountType>(request.AccountType, true, out BankAccountType accountType))
        {
            return Results.BadRequest(OperationResult.Fail("AccountType must be Checking or Savings."));
        }

        AccountOpenResult result = await bankingService.OpenAccountAsync(
            userId,
            accountType,
            request.Nickname ?? string.Empty,
            request.FundingSourceAccountId,
            request.OpeningDeposit);

        return result.IsSuccess
            ? Results.Ok(result)
            : Results.BadRequest(result);
    }).RequireRateLimiting("transfers");

    api.MapGet("/transactions", async (
        HttpContext context,
        BankingService bankingService,
        Guid? accountId,
        string? search,
        string? category,
        string? state,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc) =>
    {
        if (!TryAuthenticate(context, bankingService, out Guid userId, out _))
        {
            return Results.Unauthorized();
        }

        TransactionState? parsedState = Enum.TryParse<TransactionState>(state, true, out TransactionState current)
            ? current
            : null;

        LedgerPageResult? ledger = await bankingService.GetLedgerAsync(
            userId,
            new LedgerFilter(accountId, search, category, parsedState, fromUtc, toUtc));

        if (ledger is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(ledger.Transactions);
    });

    api.MapGet("/transfers/recipients", async (
        HttpContext context,
        BankingService bankingService) =>
    {
        if (!TryAuthenticate(context, bankingService, out Guid userId, out _))
        {
            return Results.Unauthorized();
        }

        IReadOnlyList<TransferRecipientAccount> recipients = await bankingService.GetTransferRecipientsAsync(userId);
        return Results.Ok(recipients.Select(recipient => new
        {
            accountId = recipient.AccountId,
            userId = recipient.UserId,
            recipientName = recipient.RecipientName,
            recipientEmail = recipient.RecipientEmail,
            accountNickname = recipient.AccountNickname,
            maskedAccountNumber = recipient.MaskedAccountNumber,
            accountType = recipient.AccountType.ToString()
        }));
    });

    api.MapPost("/transfers/internal", async (
        HttpContext context,
        BankingService bankingService,
        InternalTransferApiRequest request) =>
    {
        if (!TryAuthenticate(context, bankingService, out Guid userId, out UserRole role))
        {
            return Results.Unauthorized();
        }

        string idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        TransferActionResult result = await bankingService.CreateTransferAsync(
            userId,
            new TransferRequest
            {
                SourceAccountId = request.SourceAccountId,
                DestinationInternalAccountId = request.DestinationAccountId,
                Amount = request.Amount,
                Memo = request.Memo,
                ScheduledForUtc = request.ScheduledForUtc,
                Frequency = request.Frequency,
                IdempotencyKey = idempotencyKey
            },
            role);

        return result.IsSuccess
            ? Results.Ok(result)
            : Results.BadRequest(result);
    }).RequireRateLimiting("transfers");

    api.MapPost("/transfers/external", async (
        HttpContext context,
        BankingService bankingService,
        ExternalTransferApiRequest request) =>
    {
        if (!TryAuthenticate(context, bankingService, out Guid userId, out UserRole role))
        {
            return Results.Unauthorized();
        }

        string idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        TransferActionResult result = await bankingService.CreateTransferAsync(
            userId,
            new TransferRequest
            {
                SourceAccountId = request.SourceAccountId,
                DestinationExternalAccountId = request.ExternalAccountId,
                Amount = request.Amount,
                Memo = request.Memo,
                ScheduledForUtc = request.ScheduledForUtc,
                Frequency = request.Frequency,
                IdempotencyKey = idempotencyKey
            },
            role);

        return result.IsSuccess
            ? Results.Ok(result)
            : Results.BadRequest(result);
    }).RequireRateLimiting("transfers");

    api.MapPost("/transfers/peer", async (
        HttpContext context,
        BankingService bankingService,
        PeerTransferApiRequest request) =>
    {
        if (!TryAuthenticate(context, bankingService, out Guid userId, out UserRole role))
        {
            return Results.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request.RecipientEmail))
        {
            return Results.BadRequest(OperationResult.Fail("RecipientEmail is required."));
        }

        BankAccountType? preferredType = null;
        if (!string.IsNullOrWhiteSpace(request.PreferredDestinationAccountType))
        {
            bool parsed = Enum.TryParse<BankAccountType>(
                request.PreferredDestinationAccountType,
                ignoreCase: true,
                out BankAccountType candidateType);
            if (!parsed || candidateType == BankAccountType.ExternalLinked)
            {
                return Results.BadRequest(OperationResult.Fail("PreferredDestinationAccountType must be Checking or Savings."));
            }

            preferredType = candidateType;
        }

        string idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        TransferActionResult result = await bankingService.CreatePeerTransferByEmailAsync(
            userId,
            new PeerTransferByEmailRequest
            {
                SourceAccountId = request.SourceAccountId,
                RecipientEmail = request.RecipientEmail,
                PreferredDestinationAccountType = preferredType,
                Amount = request.Amount,
                Memo = request.Memo,
                ScheduledForUtc = request.ScheduledForUtc,
                Frequency = request.Frequency,
                IdempotencyKey = idempotencyKey
            },
            role);

        return result.IsSuccess
            ? Results.Ok(result)
            : Results.BadRequest(result);
    }).RequireRateLimiting("transfers");

    api.MapPost("/money/income", async (
        HttpContext context,
        BankingService bankingService,
        IncomeApiRequest request) =>
    {
        if (!TryAuthenticate(context, bankingService, out Guid userId, out _))
        {
            return Results.Unauthorized();
        }

        OperationResult result = await bankingService.AddFundsAsync(
            userId,
            request.AccountId,
            request.Amount,
            request.Source ?? "Funds Credit",
            request.Note ?? string.Empty);

        return result.IsSuccess
            ? Results.Ok(result)
            : Results.BadRequest(result);
    }).RequireRateLimiting("transfers");

    api.MapPost("/money/expense", async (
        HttpContext context,
        BankingService bankingService,
        ExpenseApiRequest request) =>
    {
        if (!TryAuthenticate(context, bankingService, out Guid userId, out _))
        {
            return Results.Unauthorized();
        }

        OperationResult result = await bankingService.SpendFundsAsync(
            userId,
            request.AccountId,
            request.Amount,
            request.MerchantName ?? "Card Purchase",
            request.Category ?? "General",
            request.Note ?? string.Empty);

        return result.IsSuccess
            ? Results.Ok(result)
            : Results.BadRequest(result);
    }).RequireRateLimiting("transfers");

    api.MapGet("/notifications", async (HttpContext context, BankingService bankingService) =>
    {
        if (!TryAuthenticate(context, bankingService, out Guid userId, out _))
        {
            return Results.Unauthorized();
        }

        DashboardSnapshot? dashboard = await bankingService.GetDashboardSnapshotAsync(userId, 30);
        if (dashboard is null)
        {
            return Results.NotFound();
        }

        return Results.Ok(dashboard.Notifications);
    });

    api.MapGet("/admin/audit-logs", async (HttpContext context, BankingService bankingService) =>
    {
        if (!TryAuthenticate(context, bankingService, out _, out UserRole role) || role != UserRole.Admin)
        {
            return Results.Unauthorized();
        }

        AdminSnapshot snapshot = await bankingService.GetAdminSnapshotAsync();
        return Results.Ok(snapshot.AuditLogs);
    });
}

static bool TryAuthenticate(
    HttpContext context,
    BankingService bankingService,
    out Guid userId,
    out UserRole role)
{
    userId = Guid.Empty;
    role = UserRole.User;

    Guid? sessionUserId = context.Session.GetCurrentUserId();
    UserRole? sessionRole = context.Session.GetCurrentUserRole();
    if (sessionUserId.HasValue && sessionRole.HasValue)
    {
        userId = sessionUserId.Value;
        role = sessionRole.Value;
        return true;
    }

    string? token = context.Request.Cookies[AuthCookieNames.AccessToken];
    if (!string.IsNullOrWhiteSpace(token) && bankingService.TryValidateAccessToken(token, out TokenPayload payload))
    {
        userId = payload.UserId;
        role = payload.Role;
        return true;
    }

    return false;
}

static string MaskAccount(string accountNumber)
{
    if (string.IsNullOrWhiteSpace(accountNumber))
    {
        return "****";
    }

    string last4 = accountNumber.Length <= 4 ? accountNumber : accountNumber[^4..];
    return $"****{last4}";
}

public sealed record InternalTransferApiRequest(
    Guid SourceAccountId,
    Guid DestinationAccountId,
    decimal Amount,
    string? Memo,
    DateTimeOffset? ScheduledForUtc,
    TransferFrequency Frequency = TransferFrequency.OneTime);

public sealed record ExternalTransferApiRequest(
    Guid SourceAccountId,
    Guid ExternalAccountId,
    decimal Amount,
    string? Memo,
    DateTimeOffset? ScheduledForUtc,
    TransferFrequency Frequency = TransferFrequency.OneTime);

public sealed record PeerTransferApiRequest(
    Guid SourceAccountId,
    string RecipientEmail,
    decimal Amount,
    string? Memo,
    DateTimeOffset? ScheduledForUtc,
    string? PreferredDestinationAccountType,
    TransferFrequency Frequency = TransferFrequency.OneTime);

public sealed record OpenAccountApiRequest(
    string? AccountType,
    string? Nickname,
    decimal OpeningDeposit,
    Guid? FundingSourceAccountId);

public sealed record IncomeApiRequest(
    Guid AccountId,
    decimal Amount,
    string? Source,
    string? Note);

public sealed record ExpenseApiRequest(
    Guid AccountId,
    decimal Amount,
    string? MerchantName,
    string? Category,
    string? Note);
