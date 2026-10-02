namespace NeuralBridge.Domain.Sessions;

public enum SessionStatus
{
    Active = 0,
    Closed = 1,
    Expired = 2,
}

/// <summary>Why a session stopped being available.</summary>
public enum SessionEndReason
{
    ClosedByOwner = 1,
    Expired = 2,
}
