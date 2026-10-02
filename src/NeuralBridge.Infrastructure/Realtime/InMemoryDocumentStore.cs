using System.Collections.Concurrent;
using NeuralBridge.Application.Abstractions;
using NeuralBridge.Domain.Documents;

namespace NeuralBridge.Infrastructure.Realtime;

/// <summary>
/// Volatile document storage with one lock per document, so concurrent writers to the same
/// session are applied one after another and versions never skip or repeat.
/// </summary>
public sealed class InMemoryDocumentStore : IDocumentStore
{
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public int Count => _entries.Count;

    public IReadOnlyCollection<Guid> SessionIds => _entries.Keys.ToArray();

    public SharedDocument GetOrCreate(Guid sessionId, DateTimeOffset now)
    {
        var entry = _entries.GetOrAdd(sessionId, id => new Entry(SharedDocument.Empty(id, now)));
        lock (entry.Gate)
        {
            return entry.Document;
        }
    }

    public TResult Update<TResult>(Guid sessionId, DateTimeOffset now, Func<SharedDocument, (SharedDocument Next, TResult Result)> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var entry = _entries.GetOrAdd(sessionId, id => new Entry(SharedDocument.Empty(id, now)));
        lock (entry.Gate)
        {
            var (next, result) = update(entry.Document);
            entry.Document = next;
            return result;
        }
    }

    public bool Remove(Guid sessionId) => _entries.TryRemove(sessionId, out _);

    private sealed class Entry(SharedDocument document)
    {
        public Lock Gate { get; } = new();

        public SharedDocument Document { get; set; } = document;
    }
}
