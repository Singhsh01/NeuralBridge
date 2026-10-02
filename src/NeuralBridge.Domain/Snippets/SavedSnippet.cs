using NeuralBridge.Domain.Common;

namespace NeuralBridge.Domain.Snippets;

/// <summary>
/// Text a signed-in user explicitly chose to keep. This is the only place where shared text
/// is persisted beyond a session's lifetime.
/// </summary>
public sealed class SavedSnippet
{
    public const int MaxTitleLength = 80;

    // EF Core
    private SavedSnippet()
    {
        UserId = string.Empty;
        Title = string.Empty;
        Content = string.Empty;
    }

    private SavedSnippet(Guid id, string userId, string title, string content, DateTimeOffset createdAt)
    {
        Id = id;
        UserId = userId;
        Title = title;
        Content = content;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public string UserId { get; private set; }

    public string Title { get; private set; }

    public string Content { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static SavedSnippet Create(string userId, string? title, string content, int maxContentLength, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0)
        {
            throw new DomainException(DomainErrorCodes.InvalidArgument, "Nothing to save — the editor is empty.");
        }

        if (content.Length > maxContentLength)
        {
            throw new DomainException(DomainErrorCodes.InvalidArgument, "The text is too long to save.");
        }

        return new SavedSnippet(Guid.NewGuid(), userId, DeriveTitle(title, content), content, now);
    }

    private static string DeriveTitle(string? title, string content)
    {
        var source = string.IsNullOrWhiteSpace(title)
            ? content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "Untitled"
            : title.Trim();
        source = new string(source.Where(c => !char.IsControl(c)).ToArray());
        if (source.Length == 0)
        {
            source = "Untitled";
        }

        return source.Length > MaxTitleLength ? string.Concat(source.AsSpan(0, MaxTitleLength - 1), "…") : source;
    }
}
