using NeuralBridge.Application.Abstractions;
using NeuralBridge.Application.Options;
using NeuralBridge.Domain.Sessions;
using NeuralBridge.Infrastructure.Security;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace NeuralBridge.Tests.Security;

public class SessionCodeGeneratorTests
{
    private readonly SessionCodeGenerator _generator = new();

    [Fact]
    public void Generated_codes_are_normalized_and_use_only_the_alphabet()
    {
        for (var i = 0; i < 1000; i++)
        {
            var code = _generator.Generate();
            Assert.Equal(SessionCode.Length, code.Value.Length);
            Assert.True(SessionCode.IsNormalized(code.Value));
            Assert.Matches("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$", code.Formatted);
        }
    }

    [Fact]
    public void Generated_codes_do_not_repeat_and_are_not_sequential()
    {
        var codes = Enumerable.Range(0, 20_000).Select(_ => _generator.Generate().Value).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());

        // Sorting order must not match generation order (would indicate a counter).
        var sorted = codes.Order(StringComparer.Ordinal).ToList();
        Assert.NotEqual(sorted, codes);
    }

    [Fact]
    public void Symbol_distribution_is_roughly_uniform()
    {
        var counts = new Dictionary<char, int>();
        const int samples = 20_000;
        for (var i = 0; i < samples; i++)
        {
            foreach (var c in _generator.Generate().Value)
            {
                counts[c] = counts.GetValueOrDefault(c) + 1;
            }
        }

        var expected = samples * SessionCode.Length / 32.0;
        Assert.Equal(32, counts.Count);
        Assert.All(counts.Values, n => Assert.InRange(n, expected * 0.9, expected * 1.1));
    }

    [Fact]
    public void Public_ids_are_128_bit_url_safe_and_unique()
    {
        var ids = Enumerable.Range(0, 1000).Select(_ => _generator.GeneratePublicId()).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Matches("^[A-Za-z0-9_-]{22}$", id));
    }
}

public class Pbkdf2SecretHasherTests
{
    [Fact]
    public void Hash_verifies_correct_secret_and_rejects_others()
    {
        var hasher = new Pbkdf2SecretHasher(iterations: 1000);
        var hash = hasher.Hash("4821");
        Assert.StartsWith("pbkdf2-sha512$1000$", hash);
        Assert.True(hasher.Verify("4821", hash));
        Assert.False(hasher.Verify("4822", hash));
        Assert.False(hasher.Verify("", hash));
    }

    [Fact]
    public void Same_secret_produces_different_salted_hashes()
    {
        var hasher = new Pbkdf2SecretHasher(iterations: 1000);
        Assert.NotEqual(hasher.Hash("same"), hasher.Hash("same"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("plain")]
    [InlineData("pbkdf2-sha512$abc$AAAA$AAAA")]
    [InlineData("pbkdf2-sha512$1000$not-base64$AAAA")]
    [InlineData("md5$1000$AAAA$AAAA")]
    public void Malformed_hashes_never_verify(string encoded)
    {
        Assert.False(new Pbkdf2SecretHasher(iterations: 1000).Verify("x", encoded));
    }

    [Fact]
    public void Default_iteration_count_meets_owasp_guidance()
    {
        Assert.True(Pbkdf2SecretHasher.DefaultIterations >= 210_000);
        Assert.Contains("$210000$", new Pbkdf2SecretHasher().Hash("1234"));
    }
}

public class TokenServiceTests
{
    private readonly TokenService _tokens = new();

    [Fact]
    public void Tokens_are_256_bit_and_unique()
    {
        var a = _tokens.GenerateToken();
        var b = _tokens.GenerateToken();
        Assert.NotEqual(a, b);
        Assert.Equal(43, a.Length); // 32 bytes, base64url without padding
    }

    [Fact]
    public void Only_the_original_token_matches_its_hash()
    {
        var token = _tokens.GenerateToken();
        var hash = _tokens.HashToken(token);
        Assert.DoesNotContain(token, hash, StringComparison.Ordinal);
        Assert.True(_tokens.Matches(token, hash));
        Assert.False(_tokens.Matches(_tokens.GenerateToken(), hash));
        Assert.False(_tokens.Matches(null, hash));
        Assert.False(_tokens.Matches(token, "not-hex"));
    }
}

public class AppRateLimiterTests
{
    [Fact]
    public void Fixed_window_limits_per_partition()
    {
        using var limiter = new AppRateLimiter(MsOptions.Create(new RateLimitOptions { JoinSessionPerMinute = 3 }));
        Assert.True(limiter.TryAcquire(RateLimitPolicy.JoinSession, "a"));
        Assert.True(limiter.TryAcquire(RateLimitPolicy.JoinSession, "a"));
        Assert.True(limiter.TryAcquire(RateLimitPolicy.JoinSession, "a"));
        Assert.False(limiter.TryAcquire(RateLimitPolicy.JoinSession, "a"));

        // Other clients and other policies are unaffected.
        Assert.True(limiter.TryAcquire(RateLimitPolicy.JoinSession, "b"));
        Assert.True(limiter.TryAcquire(RateLimitPolicy.CreateSession, "a"));
    }

    [Fact]
    public void Token_bucket_limits_document_update_bursts()
    {
        using var limiter = new AppRateLimiter(MsOptions.Create(new RateLimitOptions { DocumentUpdatesPerSecond = 5 }));
        var granted = Enumerable.Range(0, 20).Count(_ => limiter.TryAcquire(RateLimitPolicy.DocumentUpdate, "p1"));
        Assert.Equal(5, granted);
    }
}
