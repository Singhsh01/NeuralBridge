using System.ComponentModel.DataAnnotations;

namespace NeuralBridge.Application.Options;

/// <summary>Bound from <c>NeuralBridge:Sessions</c>.</summary>
public sealed class SessionOptions
{
    public const string SectionName = "NeuralBridge:Sessions";

    public static readonly int[] DefaultAllowedLifetimesMinutes = [15, 60, 480, 1440];
    public static readonly int[] DefaultExtensionStepsMinutes = [15, 60];

    /// <summary>
    /// Lifetimes offered when creating a session (minutes). Empty means the defaults
    /// (configuration binding appends to non-empty arrays, so defaults are applied after binding).
    /// </summary>
    public int[] AllowedLifetimesMinutes { get; set; } = [];

    [Range(1, 10080)]
    public int DefaultLifetimeMinutes { get; set; } = 60;

    /// <summary>Extension steps the owner can choose (minutes). Empty means the defaults.</summary>
    public int[] ExtensionStepsMinutes { get; set; } = [];

    /// <summary>Hard ceiling for creation-to-expiry, including extensions (hours).</summary>
    [Range(1, 168)]
    public int MaxTotalLifetimeHours { get; set; } = 48;

    [Range(1000, 1_000_000)]
    public int MaxContentLength { get; set; } = 50_000;

    [Range(2, 100)]
    public int MaxParticipants { get; set; } = 12;

    [Range(4, 64)]
    public int PinMinLength { get; set; } = 4;

    [Range(4, 64)]
    public int PinMaxLength { get; set; } = 32;

    [Range(1, 100)]
    public int MaxPinAttempts { get; set; } = 5;

    [Range(1, 1440)]
    public int PinLockoutMinutes { get; set; } = 5;

    [Range(5, 3600)]
    public int CleanupIntervalSeconds { get; set; } = 30;

    /// <summary>How long ended-session metadata (no content) is kept so late visitors see "expired"/"closed" instead of "not found".</summary>
    [Range(0, 10080)]
    public int EndedSessionRetentionMinutes { get; set; } = 60;

    /// <summary>How long signed-in owners' ended sessions stay in "My sessions" history (metadata only). 0 disables history.</summary>
    [Range(0, 365)]
    public int AccountHistoryRetentionDays { get; set; } = 30;

    [Range(1, 50)]
    public int CodeGenerationMaxAttempts { get; set; } = 8;

    /// <summary>Fills empty lists with defaults and normalizes order. Called after configuration binding.</summary>
    public void ApplyDefaults()
    {
        AllowedLifetimesMinutes = (AllowedLifetimesMinutes.Length == 0 ? DefaultAllowedLifetimesMinutes : AllowedLifetimesMinutes).Distinct().Order().ToArray();
        ExtensionStepsMinutes = (ExtensionStepsMinutes.Length == 0 ? DefaultExtensionStepsMinutes : ExtensionStepsMinutes).Distinct().Order().ToArray();
    }

    public TimeSpan DefaultLifetime => TimeSpan.FromMinutes(DefaultLifetimeMinutes);

    public TimeSpan MaxTotalLifetime => TimeSpan.FromHours(MaxTotalLifetimeHours);

    public TimeSpan PinLockout => TimeSpan.FromMinutes(PinLockoutMinutes);

    public TimeSpan EndedSessionRetention => TimeSpan.FromMinutes(EndedSessionRetentionMinutes);

    public TimeSpan AccountHistoryRetention => TimeSpan.FromDays(AccountHistoryRetentionDays);

    public IReadOnlyList<TimeSpan> AllowedLifetimes => (AllowedLifetimesMinutes.Length == 0 ? DefaultAllowedLifetimesMinutes : AllowedLifetimesMinutes).Select(m => TimeSpan.FromMinutes(m)).ToArray();

    public IReadOnlyList<TimeSpan> ExtensionSteps => (ExtensionStepsMinutes.Length == 0 ? DefaultExtensionStepsMinutes : ExtensionStepsMinutes).Select(m => TimeSpan.FromMinutes(m)).ToArray();
}
