using AtmMachine.Services.Abstractions;

namespace AtmMachine.Services;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
