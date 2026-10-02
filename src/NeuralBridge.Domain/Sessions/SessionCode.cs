using System.Diagnostics.CodeAnalysis;

namespace NeuralBridge.Domain.Sessions;

/// <summary>
/// A human-typeable join code: 12 symbols of Crockford Base32 (60 bits of entropy),
/// displayed as three groups of four, e.g. <c>7KQ4-M2XD-9PHT</c>.
/// </summary>
/// <remarks>
/// The alphabet has no I, L, O or U, so codes are hard to misread. When parsing, the
/// visually confusable characters are mapped (O→0, I/L→1). Hyphens, spaces and case are
/// ignored.
/// </remarks>
public readonly record struct SessionCode
{
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int Length = 12;
    public const int GroupSize = 4;

    private SessionCode(string value) => Value = value;

    /// <summary>Normalized value without separators (12 upper-case symbols).</summary>
    public string Value { get; }

    /// <summary>Display form with hyphens, e.g. <c>7KQ4-M2XD-9PHT</c>.</summary>
    public string Formatted => $"{Value[..4]}-{Value[4..8]}-{Value[8..]}";

    public override string ToString() => Formatted;

    /// <summary>Creates a code from an already-normalized value (e.g. from the generator or the database).</summary>
    public static SessionCode FromNormalized(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!IsNormalized(value))
        {
            throw new ArgumentException("Value is not a normalized session code.", nameof(value));
        }

        return new SessionCode(value);
    }

    /// <summary>
    /// Parses user input leniently (case-insensitive, separators ignored, confusable characters
    /// mapped). Returns <c>false</c> for anything that cannot be a valid code, without touching
    /// storage, so malformed input never costs a database lookup.
    /// </summary>
    public static bool TryParse(string? input, [NotNullWhen(true)] out SessionCode? code)
    {
        code = null;
        if (string.IsNullOrWhiteSpace(input) || input.Length > 40)
        {
            return false;
        }

        Span<char> buffer = stackalloc char[Length];
        var count = 0;
        foreach (var raw in input)
        {
            if (raw is '-' or ' ' or '_' or '\t')
            {
                continue;
            }

            var c = char.ToUpperInvariant(raw) switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                var other => other,
            };

            if (Alphabet.IndexOf(c, StringComparison.Ordinal) < 0 || count >= Length)
            {
                return false;
            }

            buffer[count++] = c;
        }

        if (count != Length)
        {
            return false;
        }

        code = new SessionCode(new string(buffer));
        return true;
    }

    public static bool IsNormalized(string value) =>
        value.Length == Length && value.All(c => Alphabet.Contains(c, StringComparison.Ordinal));
}
