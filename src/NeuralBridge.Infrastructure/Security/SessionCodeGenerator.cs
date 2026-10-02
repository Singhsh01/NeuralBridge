using System.Buffers.Text;
using System.Security.Cryptography;
using NeuralBridge.Application.Abstractions;
using NeuralBridge.Domain.Sessions;

namespace NeuralBridge.Infrastructure.Security;

/// <summary>
/// Draws codes from the OS CSPRNG. <see cref="RandomNumberGenerator.GetItems{T}(ReadOnlySpan{T}, int)"/>
/// picks each symbol uniformly (rejection sampling, no modulo bias), so a 12-symbol code
/// over a 32-symbol alphabet has exactly 60 bits of entropy.
/// </summary>
public sealed class SessionCodeGenerator : ISessionCodeGenerator
{
    public SessionCode Generate()
    {
        var symbols = RandomNumberGenerator.GetItems<char>(SessionCode.Alphabet, SessionCode.Length);
        return SessionCode.FromNormalized(new string(symbols));
    }

    public string GeneratePublicId()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url.EncodeToString(bytes);
    }
}
