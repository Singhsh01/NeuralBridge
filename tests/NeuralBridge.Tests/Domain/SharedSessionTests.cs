using NeuralBridge.Domain.Common;
using NeuralBridge.Domain.Sessions;

namespace NeuralBridge.Tests.Domain;

public class SharedSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);

    private static SharedSession NewSession(TimeSpan? lifetime = null, int maxParticipants = 3) =>
        SharedSession.Create(Guid.NewGuid(), "pub", SessionCode.FromNormalized("7KQ4M2XD9PHT"), Now, lifetime ?? TimeSpan.FromHours(1), maxParticipants, null, null);

    [Fact]
    public void Session_is_active_until_expiry_then_reports_expired_without_sweeper()
    {
        var session = NewSession();
        Assert.True(session.IsActive(Now.AddMinutes(59)));
        Assert.Equal(SessionStatus.Expired, session.GetStatus(Now.AddHours(1)));
        Assert.Equal(SessionStatus.Active, session.Status); // persisted status unchanged until swept
    }

    [Fact]
    public void Capacity_is_enforced_for_guests_but_not_owners()
    {
        var session = NewSession(maxParticipants: 2);
        session.AddParticipant(Guid.NewGuid(), "Host", "h", isOwner: true, null, Now);
        session.AddParticipant(Guid.NewGuid(), "G1", "h", isOwner: false, null, Now);

        var ex = Assert.Throws<DomainException>(() => session.AddParticipant(Guid.NewGuid(), "G2", "h", false, null, Now));
        Assert.Equal(DomainErrorCodes.CapacityReached, ex.Code);

        session.AddParticipant(Guid.NewGuid(), "Host device 2", "h", isOwner: true, "user-1", Now);
        Assert.Equal(3, session.ActiveParticipantCount);
    }

    [Fact]
    public void Extend_is_capped_at_max_total_lifetime()
    {
        var session = NewSession(TimeSpan.FromHours(23));
        var newExpiry = session.Extend(TimeSpan.FromHours(5), maxTotalLifetime: TimeSpan.FromHours(24), Now);
        Assert.Equal(Now.AddHours(24), newExpiry);

        var ex = Assert.Throws<DomainException>(() => session.Extend(TimeSpan.FromMinutes(15), TimeSpan.FromHours(24), Now));
        Assert.Equal(DomainErrorCodes.ExtensionLimitReached, ex.Code);
    }

    [Fact]
    public void Closing_clears_pin_and_marks_everyone_left()
    {
        var session = SharedSession.Create(Guid.NewGuid(), "pub", SessionCode.FromNormalized("7KQ4M2XD9PHT"), Now, TimeSpan.FromHours(1), 5, "hash", null);
        session.AddParticipant(Guid.NewGuid(), "Host", "h", true, null, Now);
        session.Close(Now.AddMinutes(1));

        Assert.Equal(SessionStatus.Closed, session.Status);
        Assert.Equal(SessionEndReason.ClosedByOwner, session.EndReason);
        Assert.False(session.HasPin);
        Assert.All(session.Participants, p => Assert.False(p.IsActive));
        Assert.Throws<DomainException>(() => session.SetGuestEditing(false, Now.AddMinutes(2)));
    }

    [Fact]
    public void Pin_lockout_triggers_after_max_attempts_and_resets_after_window()
    {
        var session = NewSession();
        Assert.False(session.RegisterFailedPinAttempt(Now, maxAttempts: 3, TimeSpan.FromMinutes(5)));
        Assert.False(session.RegisterFailedPinAttempt(Now, 3, TimeSpan.FromMinutes(5)));
        Assert.True(session.RegisterFailedPinAttempt(Now, 3, TimeSpan.FromMinutes(5)));
        Assert.True(session.IsPinLocked(Now.AddMinutes(4)));
        Assert.False(session.IsPinLocked(Now.AddMinutes(5)));
    }

    [Fact]
    public void Owners_cannot_be_removed()
    {
        var session = NewSession();
        var owner = session.AddParticipant(Guid.NewGuid(), "Host", "h", true, null, Now);
        var ex = Assert.Throws<DomainException>(() => session.RemoveParticipant(owner.Id, Now));
        Assert.Equal(DomainErrorCodes.CannotRemoveOwner, ex.Code);
    }

    [Theory]
    [InlineData(null, "Fallback")]
    [InlineData("   ", "Fallback")]
    [InlineData("  Ada   Lovelace ", "Ada Lovelace")]
    [InlineData("Line\nBreak\u0007", "LineBreak")]
    public void Display_names_are_sanitized(string? input, string expected)
    {
        Assert.Equal(expected, SessionParticipant.NormalizeDisplayName(input, "Fallback"));
    }

    [Fact]
    public void Display_names_are_truncated_to_the_limit()
    {
        var name = SessionParticipant.NormalizeDisplayName(new string('x', 200), "F");
        Assert.Equal(SessionParticipant.MaxDisplayNameLength, name.Length);
    }
}
