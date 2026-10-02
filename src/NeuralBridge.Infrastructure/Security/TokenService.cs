using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using NeuralBridge.Application.Abstractions;

namespace NeuralBridge.Infrastructure.Security;

/// <summary>
/// 256-bit random bearer tokens. A fast SHA-256 is fine here: unlike PINs, the tokens have
/// full entropy, so their hashes can't be brute-forced offline.
/// </summary>
public sealed class TokenService : ITokenService
{
    public string GenerateToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url.EncodeToString(bytes);
    }

    public string HashToken(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    public bool Matches(string? token, string storedHash)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(storedHash) || token.Length > 256)
        {
            return false;
        }

        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(storedHash);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
