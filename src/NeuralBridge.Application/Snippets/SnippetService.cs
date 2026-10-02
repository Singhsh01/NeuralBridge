using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NeuralBridge.Application.Abstractions;
using NeuralBridge.Application.Common;
using NeuralBridge.Application.Documents;
using NeuralBridge.Application.Options;
using NeuralBridge.Application.Sessions;
using NeuralBridge.Domain.Common;
using NeuralBridge.Domain.Snippets;

namespace NeuralBridge.Application.Snippets;

public sealed record SnippetView(Guid Id, string Title, string Content, DateTimeOffset CreatedAt)
{
    public int CharacterCount => Content.Length;
}

/// <summary>Explicit, user-initiated persistence of shared text for signed-in users.</summary>
public interface ISnippetService
{
    Task<Result<SnippetView>> SaveFromSessionAsync(string userId, ParticipantCredentials credentials, string? title, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SnippetView>> ListAsync(string userId, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string userId, Guid snippetId, CancellationToken cancellationToken = default);
}

public sealed class SnippetService : ISnippetService
{
    private readonly ISnippetRepository _repository;
    private readonly ITextSynchronizationService _sync;
    private readonly TimeProvider _time;
    private readonly SessionOptions _options;
    private readonly ILogger<SnippetService> _logger;

    public SnippetService(
        ISnippetRepository repository,
        ITextSynchronizationService sync,
        TimeProvider time,
        IOptions<SessionOptions> options,
        ILogger<SnippetService> logger)
    {
        _repository = repository;
        _sync = sync;
        _time = time;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Result<SnippetView>> SaveFromSessionAsync(string userId, ParticipantCredentials credentials, string? title, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Result<SnippetView>.Failure(SessionError.Unauthorized, "Sign in to save text to your account.");
        }

        // The participant must still be authorized for the session the text comes from.
        var document = await _sync.GetDocumentAsync(credentials, cancellationToken);
        if (!document.Succeeded)
        {
            return Result<SnippetView>.Failure(document.Error);
        }

        SavedSnippet snippet;
        try
        {
            snippet = SavedSnippet.Create(userId, title, document.Value!.Content, _options.MaxContentLength, _time.GetUtcNow());
        }
        catch (DomainException ex)
        {
            return Result<SnippetView>.Failure(SessionError.ValidationFailed, ex.Message);
        }

        await _repository.AddAsync(snippet, cancellationToken);
        _logger.LogInformation("Snippet {SnippetId} saved by user", snippet.Id);
        return Result<SnippetView>.Success(ToView(snippet));
    }

    public async Task<IReadOnlyList<SnippetView>> ListAsync(string userId, CancellationToken cancellationToken = default)
    {
        var snippets = await _repository.ListAsync(userId, cancellationToken);
        return snippets.OrderByDescending(s => s.CreatedAt).Select(ToView).ToList();
    }

    public Task<bool> DeleteAsync(string userId, Guid snippetId, CancellationToken cancellationToken = default) =>
        _repository.DeleteAsync(userId, snippetId, cancellationToken);

    private static SnippetView ToView(SavedSnippet s) => new(s.Id, s.Title, s.Content, s.CreatedAt);
}
