using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vessel.Application.Security;
using Vessel.Infrastructure.Configuration;

namespace Vessel.Infrastructure.Security;

internal sealed class Argon2PasswordHasher : IPasswordHasher
{
    private const int HashSizeInBytes = 32;
    private const int SaltSizeInBytes = 16;
    private readonly int _degreeOfParallelism;
    private readonly int _iterations;
    private readonly ILogger<Argon2PasswordHasher> _logger;
    private readonly int _memorySize;

    public Argon2PasswordHasher(IOptions<Argon2Options> options, ILogger<Argon2PasswordHasher> logger)
    {
        Argon2Options value = options.Value;
        _degreeOfParallelism = value.DegreeOfParallelism;
        _iterations = value.Iterations;
        _memorySize = value.MemorySize;
        _logger = logger;
    }

    public string HashPassword(string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSizeInBytes);
        byte[] hash = Hash(password, salt, _memorySize, _iterations, _degreeOfParallelism, HashSizeInBytes);

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"$argon2id$v=19$m={_memorySize},t={_iterations},p={_degreeOfParallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    public bool VerifyPassword(string passwordHash, string password)
    {
        if (string.IsNullOrWhiteSpace(passwordHash) || string.IsNullOrWhiteSpace(password)) return false;

        try
        {
            string[] parts = passwordHash.Split('$');
            if (parts.Length != 6 || parts[1] != "argon2id" || parts[2] != "v=19") return false;

            Dictionary<string, int> parameters = parts[3]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.Split('=', 2))
                .ToDictionary(part => part[0], part => int.Parse(part[1], System.Globalization.CultureInfo.InvariantCulture));

            byte[] salt = Convert.FromBase64String(parts[4]);
            byte[] storedHash = Convert.FromBase64String(parts[5]);
            byte[] computed = Hash(password, salt, parameters["m"], parameters["t"], parameters["p"], storedHash.Length);

            return CryptographicOperations.FixedTimeEquals(computed, storedHash);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or KeyNotFoundException)
        {
            _logger.LogWarning("Password verification failed because the stored hash could not be parsed.");
            return false;
        }
    }

    private static byte[] Hash(
        string password,
        byte[] salt,
        int memorySize,
        int iterations,
        int degreeOfParallelism,
        int hashSize)
    {
        var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = degreeOfParallelism,
            Iterations = iterations,
            MemorySize = memorySize
        };

        return argon2.GetBytes(hashSize);
    }
}
