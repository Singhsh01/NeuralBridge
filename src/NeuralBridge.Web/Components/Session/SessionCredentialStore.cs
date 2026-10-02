using System.Security.Cryptography;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using NeuralBridge.Application.Sessions;

namespace NeuralBridge.Web.Components.Session;

/// <summary>
/// Keeps a tab's participant credentials in <c>sessionStorage</c>, encrypted and
/// integrity-protected with ASP.NET Core Data Protection. They are per tab, they survive
/// reloads and reconnects, and they are gone when the tab closes. Only usable after the
/// first interactive render.
/// </summary>
public sealed class SessionCredentialStore
{
    private const string Purpose = "NeuralBridge.ParticipantCredentials.v1";
    private readonly ProtectedSessionStorage _storage;

    public SessionCredentialStore(ProtectedSessionStorage storage) => _storage = storage;

    public async Task SaveAsync(ParticipantCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        await _storage.SetAsync(Purpose, Key(credentials.PublicId), new StoredCredentials(credentials.ParticipantId, credentials.Token));
    }

    public async Task<ParticipantCredentials?> GetAsync(string publicId)
    {
        try
        {
            var result = await _storage.GetAsync<StoredCredentials>(Purpose, Key(publicId));
            return result.Success && result.Value is { } v ? new ParticipantCredentials(publicId, v.ParticipantId, v.Token) : null;
        }
        catch (CryptographicException)
        {
            // Keys rotated or value tampered with: treat as absent.
            await RemoveAsync(publicId);
            return null;
        }
    }

    public async Task RemoveAsync(string publicId)
    {
        try
        {
            await _storage.DeleteAsync(Key(publicId));
        }
        catch (InvalidOperationException)
        {
            // Called during prerender/teardown: nothing to remove.
        }
    }

    private static string Key(string publicId) => $"nb.cred.{publicId}";

    private sealed record StoredCredentials(Guid ParticipantId, string Token);
}
