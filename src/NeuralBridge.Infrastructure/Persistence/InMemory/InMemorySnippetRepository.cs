using NeuralBridge.Application.Abstractions;
using NeuralBridge.Domain.Snippets;

namespace NeuralBridge.Infrastructure.Persistence.InMemory;

public sealed class InMemorySnippetRepository : ISnippetRepository
{
    private readonly List<SavedSnippet> _snippets = [];
    private readonly Lock _gate = new();

    public Task AddAsync(SavedSnippet snippet, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _snippets.Add(snippet);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SavedSnippet>> ListAsync(string userId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<SavedSnippet>>(_snippets.Where(s => s.UserId == userId).ToList());
        }
    }

    public Task<int> DeleteAllAsync(string userId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_snippets.RemoveAll(s => s.UserId == userId));
        }
    }

    public Task<bool> DeleteAsync(string userId, Guid snippetId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_snippets.RemoveAll(s => s.Id == snippetId && s.UserId == userId) > 0);
        }
    }
}
