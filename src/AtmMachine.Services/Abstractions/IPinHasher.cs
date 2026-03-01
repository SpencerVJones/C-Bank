namespace AtmMachine.Services.Abstractions;

public interface IPinHasher
{
    (string Hash, string Salt) CreateHash(string pin);
    bool Verify(string pin, string salt, string expectedHash);
}
