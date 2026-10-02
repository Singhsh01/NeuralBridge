using NeuralBridge.Domain.Documents;

namespace NeuralBridge.Application.Documents;

/// <param name="BaseVersion">The document version the client's text was derived from.</param>
public sealed record DocumentEdit(string Content, long BaseVersion, Guid ParticipantId);

/// <param name="Document">The resulting document state.</param>
/// <param name="OverwroteConcurrentChange">The edit was based on an older version and replaced newer text.</param>
public sealed record MergeResult(SharedDocument Document, bool OverwroteConcurrentChange);

/// <summary>
/// Decides how an incoming edit combines with the current document. The MVP uses
/// last-write-wins. Operational transformation or a CRDT can replace it here without
/// touching transport, UI or authorization code. (Clients would then send operations
/// instead of whole text.)
/// </summary>
public interface IDocumentMergeStrategy
{
    MergeResult Merge(SharedDocument current, DocumentEdit edit, DateTimeOffset now);
}

/// <summary>
/// Last-write-wins: the most recent accepted write becomes the document. The client sends
/// the version it based its edit on, so a write that overwrote newer text is detected and
/// reported, but it is still applied.
/// </summary>
public sealed class LastWriteWinsMergeStrategy : IDocumentMergeStrategy
{
    public MergeResult Merge(SharedDocument current, DocumentEdit edit, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(edit);
        var conflict = edit.BaseVersion < current.Version && current.LastEditorParticipantId != edit.ParticipantId;
        return new MergeResult(current.WithContent(edit.Content, edit.ParticipantId, now), conflict);
    }
}
