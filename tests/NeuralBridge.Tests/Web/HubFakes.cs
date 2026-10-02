using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using NeuralBridge.Web.Hubs;

namespace NeuralBridge.Tests.Web;

internal sealed class FakeCallerContext(string connectionId) : HubCallerContext
{
    public bool Aborted { get; private set; }

    public override string ConnectionId { get; } = connectionId;

    public override string? UserIdentifier => null;

    public override ClaimsPrincipal? User => null;

    public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

    public override IFeatureCollection Features { get; } = new FeatureCollection();

    public override CancellationToken ConnectionAborted => CancellationToken.None;

    public override void Abort() => Aborted = true;
}

internal sealed class RecordingGroups : IGroupManager
{
    public List<(string ConnectionId, string Group)> Added { get; } = [];

    public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        Added.Add((connectionId, groupName));
        return Task.CompletedTask;
    }

    public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Records which audience (group/except/client) each hub message was addressed to.</summary>
internal sealed class RecordingHubContext : IHubContext<SessionHub, ISessionHubClient>
{
    private readonly RecordingClients _clients;

    public RecordingHubContext() => _clients = new RecordingClients(Sent);

    public List<(string Audience, string Method, object? Payload)> Sent { get; } = [];

    public IHubClients<ISessionHubClient> Clients => _clients;

    public IGroupManager Groups { get; } = new RecordingGroups();
}

internal sealed class RecordingClients(List<(string Audience, string Method, object? Payload)> sent) : IHubClients<ISessionHubClient>
{
    public ISessionHubClient All => Proxy("all");

    public ISessionHubClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => Proxy("all-except");

    public ISessionHubClient Client(string connectionId) => Proxy($"client:{connectionId}");

    public ISessionHubClient Clients(IReadOnlyList<string> connectionIds) => Proxy("clients");

    public ISessionHubClient Group(string groupName) => Proxy($"group:{groupName}");

    public ISessionHubClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) =>
        Proxy($"group:{groupName}|except:{string.Join(',', excludedConnectionIds)}");

    public ISessionHubClient Groups(IReadOnlyList<string> groupNames) => Proxy("groups");

    public ISessionHubClient User(string userId) => Proxy("user");

    public ISessionHubClient Users(IReadOnlyList<string> userIds) => Proxy("users");

    private RecordingClient Proxy(string audience) => new(sent, audience);
}

internal sealed class RecordingClient(List<(string Audience, string Method, object? Payload)> sent, string audience) : ISessionHubClient
{
    public Task DocumentChanged(HubDocument document) => Record(nameof(DocumentChanged), document);

    public Task Typing(string displayName) => Record(nameof(Typing), displayName);

    public Task ParticipantsChanged() => Record(nameof(ParticipantsChanged), null);

    public Task SessionSettingsChanged() => Record(nameof(SessionSettingsChanged), null);

    public Task SessionEnded(string reason) => Record(nameof(SessionEnded), reason);

    public Task Removed() => Record(nameof(Removed), null);

    private Task Record(string method, object? payload)
    {
        lock (sent)
        {
            sent.Add((audience, method, payload));
        }

        return Task.CompletedTask;
    }
}
