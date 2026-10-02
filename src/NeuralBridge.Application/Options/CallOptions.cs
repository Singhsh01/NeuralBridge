using System.ComponentModel.DataAnnotations;

namespace NeuralBridge.Application.Options;

/// <summary>Bound from <c>NeuralBridge:Calls</c>. Audio/video calls are peer-to-peer WebRTC; the server only relays signaling.</summary>
public sealed class CallOptions
{
    public const string SectionName = "NeuralBridge:Calls";

    public bool Enabled { get; set; } = true;

    /// <summary>Calls are a full mesh (every member connects to every other), so keep this small.</summary>
    [Range(2, 12)]
    public int MaxCallParticipants { get; set; } = 6;

    /// <summary>
    /// ICE servers handed to browsers. STUN discovers public addresses; add a TURN server for
    /// networks where direct connections fail (corporate firewalls, symmetric NAT). Empty means the default public STUN server.
    /// </summary>
    public IceServerOptions[] IceServers { get; set; } = [];

    [Range(1024, 262_144)]
    public int MaxSignalBytes { get; set; } = 65_536;

    public IReadOnlyList<IceServerOptions> EffectiveIceServers =>
        IceServers.Length > 0 ? IceServers : [new IceServerOptions { Urls = ["stun:stun.l.google.com:19302"] }];
}

public sealed class IceServerOptions
{
    public string[] Urls { get; set; } = [];

    /// <summary>TURN only. Prefer short-lived credentials; these values are sent to browsers in the call.</summary>
    public string? Username { get; set; }

    public string? Credential { get; set; }
}
