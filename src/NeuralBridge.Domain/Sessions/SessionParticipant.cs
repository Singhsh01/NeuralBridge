namespace NeuralBridge.Domain.Sessions;

/// <summary>
/// A device/browser tab that joined a session. It is identified by a random id. Its bearer
/// token is stored only as a hash.
/// </summary>
public sealed class SessionParticipant
{
    public const int MaxDisplayNameLength = 40;

    // EF Core
    private SessionParticipant()
    {
        DisplayName = string.Empty;
        TokenHash = string.Empty;
    }

    internal SessionParticipant(
        Guid id,
        Guid sessionId,
        string displayName,
        string tokenHash,
        bool isOwner,
        string? userId,
        DateTimeOffset joinedAt)
    {
        Id = id;
        SessionId = sessionId;
        DisplayName = displayName;
        TokenHash = tokenHash;
        IsOwner = isOwner;
        UserId = userId;
        JoinedAt = joinedAt;
    }

    public Guid Id { get; private set; }

    public Guid SessionId { get; private set; }

    public string DisplayName { get; private set; }

    /// <summary>SHA-256 (hex) of the participant's bearer token.</summary>
    public string TokenHash { get; private set; }

    public bool IsOwner { get; private set; }

    /// <summary>Identity user id when the participant was signed in (owners of account sessions).</summary>
    public string? UserId { get; private set; }

    public DateTimeOffset JoinedAt { get; private set; }

    public DateTimeOffset? LeftAt { get; private set; }

    public bool IsRemoved { get; private set; }

    /// <summary>Participants who left or were removed no longer count toward capacity and cannot act.</summary>
    public bool IsActive => LeftAt is null && !IsRemoved;

    internal void MarkLeft(DateTimeOffset now) => LeftAt ??= now;

    internal void MarkRemoved(DateTimeOffset now)
    {
        IsRemoved = true;
        LeftAt ??= now;
    }

    /// <summary>Trims, collapses whitespace, strips control characters and applies a fallback.</summary>
    public static string NormalizeDisplayName(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var cleaned = new string(name.Where(c => !char.IsControl(c)).ToArray());
        cleaned = string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (cleaned.Length == 0)
        {
            return fallback;
        }

        return cleaned.Length > MaxDisplayNameLength ? cleaned[..MaxDisplayNameLength] : cleaned;
    }
}
