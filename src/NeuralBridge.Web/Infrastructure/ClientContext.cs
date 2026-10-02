using Microsoft.AspNetCore.Components.Server.Circuits;
using NeuralBridge.Web.Components.Session;

namespace NeuralBridge.Web.Infrastructure;

/// <summary>
/// Per-circuit (scoped) client information used as the rate-limit partition key. Blazor
/// circuit events never pass through HTTP middleware, so the remote address is captured
/// once when the circuit's connection comes up.
/// </summary>
public sealed class ClientContext
{
    public string ClientKey { get; private set; } = "unknown";

    public string CircuitId { get; private set; } = Guid.NewGuid().ToString("N");

    internal void Initialize(string? remoteAddress, string circuitId)
    {
        if (!string.IsNullOrWhiteSpace(remoteAddress))
        {
            ClientKey = remoteAddress;
        }

        CircuitId = circuitId;
    }

    public static string FromHttpContext(HttpContext? context) =>
        context?.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

/// <summary>Captures client info when a circuit opens and forwards connection up/down to workspace presence.</summary>
internal sealed class ClientContextCircuitHandler : CircuitHandler
{
    private readonly ClientContext _client;
    private readonly IHttpContextAccessor _accessor;
    private readonly WorkspacePresence _presence;
    private readonly ILogger<ClientContextCircuitHandler> _logger;

    public ClientContextCircuitHandler(ClientContext client, IHttpContextAccessor accessor, WorkspacePresence presence, ILogger<ClientContextCircuitHandler> logger)
    {
        _client = client;
        _accessor = accessor;
        _presence = presence;
        _logger = logger;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken) =>
        _presence.OnConnectionChangedAsync(connected: false);

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken) =>
        _presence.OnConnectionChangedAsync(connected: true);

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken) =>
        _presence.LeaveAsync();

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        // During circuit start the accessor exposes the SignalR (WebSocket) request context.
        var address = ClientContext.FromHttpContext(_accessor.HttpContext);
        _client.Initialize(address, circuit.Id);
        _logger.LogDebug("Circuit opened; client address resolved: {Resolved}", address != "unknown"); // never log the address itself
        return Task.CompletedTask;
    }
}
