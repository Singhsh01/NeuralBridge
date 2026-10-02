namespace NeuralBridge.Application.Common;

/// <summary>Stable error categories returned by application services and mapped to UI states and HTTP codes.</summary>
public enum SessionError
{
    None = 0,

    /// <summary>The input cannot be a join code (malformed). No lookup happened.</summary>
    InvalidCode,

    /// <summary>Deliberately ambiguous: unknown code, missing PIN or wrong PIN (anti-enumeration).</summary>
    NotFoundOrPinIncorrect,
    PinLocked,
    Expired,
    Closed,
    CapacityReached,
    RateLimited,

    /// <summary>Credentials are missing, unknown or don't match.</summary>
    Unauthorized,

    /// <summary>Authenticated, but not allowed (e.g. not the owner, removed, read-only).</summary>
    Forbidden,
    Removed,
    ReadOnly,
    ValidationFailed,
    NotFound,
    ExtensionLimitReached,
}

public class Result
{
    protected Result(SessionError error, string? message)
    {
        Error = error;
        Message = message;
    }

    public SessionError Error { get; }

    public string? Message { get; }

    public bool Succeeded => Error == SessionError.None;

    public static Result Success() => new(SessionError.None, null);

    public static Result Failure(SessionError error, string? message = null) => new(error, message ?? ErrorMessages.For(error));
}

public sealed class Result<T> : Result
{
    private Result(T? value, SessionError error, string? message)
        : base(error, message)
    {
        Value = value;
    }

    public T? Value { get; }

    public static Result<T> Success(T value) => new(value, SessionError.None, null);

    public static new Result<T> Failure(SessionError error, string? message = null) =>
        new(default, error, message ?? ErrorMessages.For(error));

    public static implicit operator Result<T>(T value) => Success(value);
}

/// <summary>Friendly, non-revealing default messages for each error.</summary>
public static class ErrorMessages
{
    public static string For(SessionError error) => error switch
    {
        SessionError.None => string.Empty,
        SessionError.InvalidCode => "That doesn't look like a session ID. Session IDs have 12 characters, like 7KQ4-M2XD-9PHT.",
        SessionError.NotFoundOrPinIncorrect => "We couldn't open a session with that ID and PIN. Check the session ID, and the PIN if the session has one.",
        SessionError.PinLocked => "Too many incorrect attempts. Please wait a few minutes and try again.",
        SessionError.Expired => "This session has expired and its content has been removed.",
        SessionError.Closed => "The owner closed this session and its content has been removed.",
        SessionError.CapacityReached => "This session is full. Ask the owner to remove an inactive participant.",
        SessionError.RateLimited => "You're doing that a little too often. Please wait a moment and try again.",
        SessionError.Unauthorized => "Your access to this session could not be verified. Please join again.",
        SessionError.Forbidden => "Only the session owner can do that.",
        SessionError.Removed => "The owner removed you from this session.",
        SessionError.ReadOnly => "The owner has turned off editing for guests.",
        SessionError.ValidationFailed => "Some of the information provided isn't valid.",
        SessionError.NotFound => "We couldn't find that.",
        SessionError.ExtensionLimitReached => "This session has reached its maximum lifetime.",
        _ => "Something went wrong.",
    };
}
