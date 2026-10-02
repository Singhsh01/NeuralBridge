namespace NeuralBridge.Domain.Common;

/// <summary>
/// Raised when an operation would violate a domain invariant. <see cref="Code"/> is a stable,
/// machine-readable identifier that upper layers translate into user-facing messages.
/// </summary>
public sealed class DomainException : Exception
{
    public DomainException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public DomainException()
        : this("domain_error", "A domain rule was violated.")
    {
    }

    public DomainException(string message)
        : this("domain_error", message)
    {
    }

    public DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = "domain_error";
    }

    public string Code { get; } = "domain_error";
}

public static class DomainErrorCodes
{
    public const string SessionNotActive = "session_not_active";
    public const string CapacityReached = "capacity_reached";
    public const string ParticipantNotFound = "participant_not_found";
    public const string CannotRemoveOwner = "cannot_remove_owner";
    public const string ExtensionLimitReached = "extension_limit_reached";
    public const string InvalidArgument = "invalid_argument";
}
