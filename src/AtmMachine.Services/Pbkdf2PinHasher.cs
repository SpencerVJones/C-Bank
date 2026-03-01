using System.Security.Cryptography;
using AtmMachine.Services.Abstractions;

namespace AtmMachine.Services;

public sealed class Pbkdf2PinHasher : IPinHasher
{
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;
    private const int Iterations = 120_000;

    public (string Hash, string Salt) CreateHash(string pin)
    {
        ValidatePinFormat(pin);

        Span<byte> salt = stackalloc byte[SaltSizeBytes];
        RandomNumberGenerator.Fill(salt);

        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            pin,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSizeBytes);

        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public bool Verify(string pin, string salt, string expectedHash)
    {
        if (string.IsNullOrWhiteSpace(pin) ||
            string.IsNullOrWhiteSpace(salt) ||
            string.IsNullOrWhiteSpace(expectedHash))
        {
            return false;
        }

        try
        {
            byte[] saltBytes = Convert.FromBase64String(salt);
            byte[] expectedBytes = Convert.FromBase64String(expectedHash);
            byte[] actualBytes = Rfc2898DeriveBytes.Pbkdf2(
                pin,
                saltBytes,
                Iterations,
                HashAlgorithmName.SHA256,
                expectedBytes.Length);

            return CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void ValidatePinFormat(string pin)
    {
        bool isValid = pin.Length == 4 && pin.All(char.IsDigit);
        if (!isValid)
        {
            throw new ArgumentException("PIN must be exactly 4 digits.", nameof(pin));
        }
    }
}
