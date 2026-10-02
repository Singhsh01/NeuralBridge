namespace NeuralBridge.Web.Components.Session;

/// <summary>Reply to the browser editor after a debounced send (serialized as camelCase JSON).</summary>
/// <param name="Ok">The text was accepted (or was unchanged).</param>
/// <param name="Version">The document version after the send.</param>
/// <param name="Retry">Transient refusal (rate limit): keep the text and resend shortly.</param>
public sealed record EditAck(bool Ok, long Version, bool Retry)
{
    public static EditAck Accepted(long version) => new(true, version, false);

    public static EditAck RetryLater() => new(false, 0, true);

    public static EditAck Rejected() => new(false, 0, false);
}
