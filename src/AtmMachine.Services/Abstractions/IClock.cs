namespace AtmMachine.Services.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
