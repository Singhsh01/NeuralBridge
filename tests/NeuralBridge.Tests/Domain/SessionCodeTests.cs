using NeuralBridge.Domain.Sessions;

namespace NeuralBridge.Tests.Domain;

public class SessionCodeTests
{
    [Theory]
    [InlineData("7KQ4-M2XD-9PHT", "7KQ4M2XD9PHT")]
    [InlineData("7kq4m2xd9pht", "7KQ4M2XD9PHT")]
    [InlineData(" 7KQ4 M2XD 9PHT ", "7KQ4M2XD9PHT")]
    [InlineData("OKQ4-M2XD-9PHT", "0KQ4M2XD9PHT")] // O → 0
    [InlineData("IKQ4-M2XD-9PHL", "1KQ4M2XD9PH1")] // I/L → 1
    public void TryParse_normalizes_lenient_input(string input, string expected)
    {
        Assert.True(SessionCode.TryParse(input, out var code));
        Assert.Equal(expected, code!.Value.Value);
        Assert.Equal($"{expected[..4]}-{expected[4..8]}-{expected[8..]}", code.Value.Formatted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("7KQ4-M2XD-9PH")]      // too short
    [InlineData("7KQ4-M2XD-9PHTX")]    // too long
    [InlineData("7KQ4-M2XD-9PHU")]     // U is not in the alphabet
    [InlineData("7KQ4-M2XD-9PH!")]
    [InlineData("<script>alert</script>")]
    public void TryParse_rejects_invalid_input(string? input)
    {
        Assert.False(SessionCode.TryParse(input, out _));
    }

    [Fact]
    public void Alphabet_has_32_unambiguous_symbols()
    {
        Assert.Equal(32, SessionCode.Alphabet.Distinct().Count());
        foreach (var ambiguous in "ILOU")
        {
            Assert.DoesNotContain(ambiguous, SessionCode.Alphabet);
        }
    }
}
