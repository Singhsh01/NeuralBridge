namespace NeuralBridge.Tests.TestSupport;

/// <summary>Deterministic clock for tests (avoids a dependency on Microsoft.Extensions.TimeProvider.Testing).</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public FakeTimeProvider(DateTimeOffset? start = null) =>
        _now = start ?? new DateTimeOffset(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public void SetUtcNow(DateTimeOffset value) => _now = value;
}
