namespace AtmMachine.Services.Models;

public sealed class AtmSecurityOptions
{
    public int MaxFailedPinAttempts { get; init; } = 3;
    public TimeSpan LockoutDuration { get; init; } = TimeSpan.FromMinutes(5);
}
