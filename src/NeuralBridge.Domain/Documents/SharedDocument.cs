namespace NeuralBridge.Domain.Documents;

/// <summary>
/// The live text of a session. Instances live only in volatile storage. The version increases
/// by exactly one per accepted write, so clients can tell which state their edit was based on.
/// </summary>
public sealed record SharedDocument(
    Guid SessionId,
    string Content,
    long Version,
    DateTimeOffset UpdatedAt,
    Guid? LastEditorParticipantId)
{
    public static SharedDocument Empty(Guid sessionId, DateTimeOffset now) =>
        new(sessionId, string.Empty, 0, now, null);

    public SharedDocument WithContent(string content, Guid editorParticipantId, DateTimeOffset now) =>
        this with
        {
            Content = content,
            Version = Version + 1,
            UpdatedAt = now,
            LastEditorParticipantId = editorParticipantId,
        };
}
