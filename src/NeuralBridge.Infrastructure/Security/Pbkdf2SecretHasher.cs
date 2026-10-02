using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NeuralBridge.Application.Abstractions;

namespace NeuralBridge.Infrastructure.Security;

/// <summary>
/// PBKDF2-HMAC-SHA512 with a per-secret 128-bit salt. The encoded format carries its
/// parameters (<c>pbkdf2-sha512$iterations$salt$hash</c>), so the iteration count can be
/// raised later without invalidating existing hashes.
/// </summary>
public sealed class Pbkdf2SecretHasher : ISecretHasher
{
    public const int DefaultIterations = 210_000; // OWASP 2023+ recommendation for PBKDF2-HMAC-SHA512
    private const string Scheme = "pbkdf2-sha512";
    private const int SaltSize = 16;
    private const int HashSize = 32;

    private readonly int _iterations;

    public Pbkdf2SecretHasher()
        : this(DefaultIterations)
    {
    }

    /// <summary>Lower iteration counts are only for fast unit tests.</summary>
    public Pbkdf2SecretHasher(int iterations)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1000);
        _iterations = iterations;
    }

    public string Hash(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Derive(secret, salt, _iterations);
        return string.Join('$', Scheme, _iterations.ToString(CultureInfo.InvariantCulture), Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    public bool Verify(string secret, string encodedHash)
    {
        if (secret is null || string.IsNullOrEmpty(encodedHash))
        {
            return false;
        }

        var parts = encodedHash.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) || iterations < 1000)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Derive(secret, salt, iterations);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Derive(string secret, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, iterations, HashAlgorithmName.SHA512, HashSize);
}
