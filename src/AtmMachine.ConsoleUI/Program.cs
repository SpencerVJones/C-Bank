using System.Globalization;
using System.Text;
using AtmMachine.Data;
using AtmMachine.Domain.Abstractions;
using AtmMachine.Domain.Models;
using AtmMachine.Services;
using AtmMachine.Services.Abstractions;
using AtmMachine.Services.Models;

namespace AtmMachine.ConsoleUI;

internal static class Program
{
    private const int StatementRowLimit = 20;

    private static async Task Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "Console ATM";

        IClock clock = new SystemClock();
        IPinHasher pinHasher = new Pbkdf2PinHasher();
        string dataPath = Path.Combine(Directory.GetCurrentDirectory(), "data", "accounts.json");
        IAccountRepository repository = new JsonAccountRepository(dataPath);
        AtmService atmService = new(
            repository,
            pinHasher,
            clock,
            new AtmSecurityOptions
            {
                MaxFailedPinAttempts = 3,
                LockoutDuration = TimeSpan.FromMinutes(5)
            });

        await EnsureSeedDataAsync(repository, pinHasher, clock);
        await RunApplicationAsync(atmService);
    }

    private static async Task EnsureSeedDataAsync(
        IAccountRepository repository,
        IPinHasher pinHasher,
        IClock clock)
    {
        if (await repository.AnyAccountsAsync())
        {
            return;
        }

        IReadOnlyList<Account> defaultAccounts = SampleAccountFactory.CreateDefaultAccounts(pinHasher, clock);
        await repository.SeedAsync(defaultAccounts);
    }

    private static async Task RunApplicationAsync(AtmService atmService)
    {
        while (true)
        {
            DrawBanner();
            Account? authenticatedAccount = await LoginAsync(atmService);
            if (authenticatedAccount is null)
            {
                WriteInfo("Goodbye.");
                return;
            }

            bool shouldExit = await RunSessionAsync(atmService, authenticatedAccount);
            if (shouldExit)
            {
                WriteInfo("Goodbye.");
                return;
            }
        }
    }

    private static async Task<Account?> LoginAsync(AtmService atmService)
    {
        while (true)
        {
            WritePrompt("Enter card number (16 digits) or Q to quit: ");
            string? cardNumber = Console.ReadLine()?.Trim();

            if (string.Equals(cardNumber, "q", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(cardNumber))
            {
                WriteError("Card number is required.");
                continue;
            }

            string pin = ReadHiddenLine("Enter 4-digit PIN: ");
            AuthenticationResult authResult = await atmService.AuthenticateAsync(cardNumber, pin);

            if (authResult.IsSuccess && authResult.Account is not null)
            {
                WriteSuccess(
                    $"Welcome {authResult.Account.FirstName} {authResult.Account.LastName} ({MaskCardNumber(authResult.Account.CardNumber)})");
                return authResult.Account;
            }

            if (authResult.LockedUntilUtc.HasValue)
            {
                DateTimeOffset localLockTime = authResult.LockedUntilUtc.Value.ToLocalTime();
                WriteError(
                    $"{authResult.Message} Try again after {localLockTime:yyyy-MM-dd HH:mm:ss}.");
            }
            else
            {
                WriteError(authResult.Message);
            }
        }
    }

    private static async Task<bool> RunSessionAsync(AtmService atmService, Account account)
    {
        while (true)
        {
            DrawMenu(account);
            int option = ReadMenuSelection(1, 6);

            switch (option)
            {
                case 1:
                    await HandleDepositAsync(atmService, account);
                    break;
                case 2:
                    await HandleWithdrawalAsync(atmService, account);
                    break;
                case 3:
                    ShowBalance(account);
                    break;
                case 4:
                    ShowStatement(atmService, account);
                    break;
                case 5:
                    WriteInfo("Logging out...");
                    return false;
                case 6:
                    return true;
            }
        }
    }

    private static async Task HandleDepositAsync(AtmService atmService, Account account)
    {
        decimal amount = ReadMoney("Deposit amount: ");
        TransactionResult result = await atmService.DepositAsync(account, amount);
        if (!result.IsSuccess)
        {
            WriteError(result.Message);
            return;
        }

        WriteSuccess($"{result.Message} New balance: {result.CurrentBalance.ToString("C", CultureInfo.CurrentCulture)}");
    }

    private static async Task HandleWithdrawalAsync(AtmService atmService, Account account)
    {
        decimal amount = ReadMoney("Withdrawal amount: ");
        TransactionResult result = await atmService.WithdrawAsync(account, amount);
        if (!result.IsSuccess)
        {
            WriteError(result.Message);
            return;
        }

        WriteSuccess($"{result.Message} New balance: {result.CurrentBalance.ToString("C", CultureInfo.CurrentCulture)}");
    }

    private static void ShowBalance(Account account)
    {
        WriteInfo($"Current balance: {account.Balance.ToString("C", CultureInfo.CurrentCulture)}");
    }

    private static void ShowStatement(AtmService atmService, Account account)
    {
        IReadOnlyList<TransactionRecord> statement = atmService.GetStatement(account, StatementRowLimit);
        if (statement.Count == 0)
        {
            WriteInfo("No transactions yet.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("+---------------------+------------+--------------+--------------+----------------------+");
        Console.WriteLine("| Timestamp (Local)   | Type       | Amount       | Balance      | Description          |");
        Console.WriteLine("+---------------------+------------+--------------+--------------+----------------------+");

        foreach (TransactionRecord transaction in statement)
        {
            string timestamp = transaction.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            string type = transaction.Type.ToString();
            string amount = transaction.Amount.ToString("C", CultureInfo.CurrentCulture);
            string balance = transaction.BalanceAfter.ToString("C", CultureInfo.CurrentCulture);
            string description = Fit(transaction.Description, 20);

            Console.WriteLine(
                $"| {Fit(timestamp, 19)} | {Fit(type, 10)} | {Fit(amount, 12)} | {Fit(balance, 12)} | {Fit(description, 20)} |");
        }

        Console.WriteLine("+---------------------+------------+--------------+--------------+----------------------+");

        decimal depositTotal = statement
            .Where(transaction => transaction.Type == TransactionType.Deposit)
            .Sum(transaction => transaction.Amount);
        decimal withdrawalTotal = statement
            .Where(transaction => transaction.Type == TransactionType.Withdrawal)
            .Sum(transaction => transaction.Amount);

        Console.WriteLine($"Deposits in view: {depositTotal.ToString("C", CultureInfo.CurrentCulture)}");
        Console.WriteLine($"Withdrawals in view: {withdrawalTotal.ToString("C", CultureInfo.CurrentCulture)}");
        Console.WriteLine($"Current balance: {account.Balance.ToString("C", CultureInfo.CurrentCulture)}");
    }

    private static void DrawBanner()
    {
        Console.Clear();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("============================================");
        Console.WriteLine("                Console ATM                 ");
        Console.WriteLine("============================================");
        Console.ResetColor();
        Console.WriteLine("Secure login, persistent accounts, and statements.");
        Console.WriteLine();
    }

    private static void DrawMenu(Account account)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("--------------------------------------------");
        Console.WriteLine($"Logged in as: {account.FirstName} {account.LastName} ({MaskCardNumber(account.CardNumber)})");
        Console.WriteLine("--------------------------------------------");
        Console.ResetColor();
        Console.WriteLine("1. Deposit");
        Console.WriteLine("2. Withdraw");
        Console.WriteLine("3. Show Balance");
        Console.WriteLine("4. View Statement");
        Console.WriteLine("5. Logout");
        Console.WriteLine("6. Exit");
        Console.WriteLine();
    }

    private static int ReadMenuSelection(int min, int max)
    {
        while (true)
        {
            WritePrompt($"Choose an option ({min}-{max}): ");
            string? input = Console.ReadLine();

            if (int.TryParse(input, out int value) && value >= min && value <= max)
            {
                return value;
            }

            WriteError("Invalid menu option.");
        }
    }

    private static decimal ReadMoney(string prompt)
    {
        while (true)
        {
            WritePrompt(prompt);
            string? input = Console.ReadLine();
            bool parsed = decimal.TryParse(
                input,
                NumberStyles.Number | NumberStyles.AllowCurrencySymbol,
                CultureInfo.CurrentCulture,
                out decimal value);

            if (parsed)
            {
                return value;
            }

            WriteError("Invalid amount format.");
        }
    }

    private static string ReadHiddenLine(string prompt)
    {
        WritePrompt(prompt);
        StringBuilder value = new();

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return value.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                {
                    value.Length--;
                    Console.Write("\b \b");
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                value.Append(key.KeyChar);
                Console.Write('*');
            }
        }
    }

    private static void WritePrompt(string message)
    {
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write(message);
        Console.ResetColor();
    }

    private static void WriteSuccess(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    private static void WriteError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    private static void WriteInfo(string message)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    private static string MaskCardNumber(string cardNumber)
    {
        if (string.IsNullOrWhiteSpace(cardNumber))
        {
            return "****";
        }

        if (cardNumber.Length <= 4)
        {
            return new string('*', cardNumber.Length);
        }

        string trailing = cardNumber[^4..];
        return $"{new string('*', cardNumber.Length - 4)}{trailing}";
    }

    private static string Fit(string value, int width)
    {
        if (value.Length > width)
        {
            return value[..(width - 3)] + "...";
        }

        return value.PadRight(width);
    }
}
