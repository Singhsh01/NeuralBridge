using NeuralBridge.Application.Common;
using NeuralBridge.Application.Realtime;
using NeuralBridge.Application.Sessions;
using NeuralBridge.Domain.Sessions;
using NeuralBridge.Tests.TestSupport;

namespace NeuralBridge.Tests.Sessions;

public class SessionOwnerServiceTests
{
    [Fact]
    public async Task Guests_cannot_perform_any_owner_operation()
    {
        using var app = new TestApp();
        var created = await app.CreateSessionAsync();
        var guest = (await app.JoinAsync(created.FormattedCode)).Credentials;
        var ownerId = created.Credentials.ParticipantId;

        Assert.Equal(SessionError.Forbidden, (await app.Owner.CloseAsync(guest)).Error);
        Assert.Equal(SessionError.Forbidden, (await app.Owner.ExtendAsync(guest, 15)).Error);
        Assert.Equal(SessionError.Forbidden, (await app.Owner.RemoveParticipantAsync(guest, ownerId)).Error);
        Assert.Equal(SessionError.Forbidden, (await app.Owner.SetGuestEditingAsync(guest, false)).Error);
        Assert.Equal(SessionError.Forbidden, (await app.Owner.RotateCodeAsync(guest)).Error);
        Assert.Equal(SessionError.Forbidden, (await app.Owner.SetPinAsync(guest, "9999")).Error);
        Assert.Equal(SessionError.Forbidden, (await app.Owner.RemovePinAsync(guest)).Error);

        var session = await app.LoadAsync(created.PublicId);
        Assert.Equal(SessionStatus.Active, session.Status);
        Assert.False(session.HasPin);
    }

    [Fact]
    public async Task Forged_or_cross_session_credentials_are_unauthorized()
    {
        using var app = new TestApp();
        var a = await app.CreateSessionAsync();
        var b = await app.CreateSessionAsync();

        var wrongToken = a.Credentials with { Token = app.Tokens.GenerateToken() };
        var otherSessionsOwner = b.Credentials with { PublicId = a.PublicId };
        var unknownSession = a.Credentials with { PublicId = "does-not-exist" };

        Assert.Equal(SessionError.Unauthorized, (await app.Owner.CloseAsync(wrongToken)).Error);
        Assert.Equal(SessionError.Unauthorized, (await app.Owner.CloseAsync(otherSessionsOwner)).Error);
        Assert.Equal(SessionError.Unauthorized, (await app.Owner.CloseAsync(unknownSession)).Error);
        Assert.Equal(SessionStatus.Active, (await app.LoadAsync(a.PublicId)).Status);
    }

    [Fact]
    public async Task Close_ends_session_purges_content_and_notifies()
    {
        using var app = new TestApp();
        var created = await app.CreateSessionAsync();
        var guest = (await app.JoinAsync(created.FormattedCode)).Credentials;
        await app.Sync.UpdateAsync(guest, "secret notes", 0, "guest-circuit");

        var result = await app.Owner.CloseAsync(created.Credentials);

        Assert.True(result.Succeeded);
        Assert.Equal(0, app.Documents.Count);
        var ended = Assert.Single(app.NotificationsOf<SessionEndedNotification>());
        Assert.Equal(SessionEndReason.ClosedByOwner, ended.Reason);
        Assert.Equal(SessionError.Closed, (await app.Sync.GetDocumentAsync(guest)).Error);
    }

    [Fact]
    public async Task Extend_adds_time_and_respects_the_ceiling()
    {
        using var app = new TestApp(o => o.MaxTotalLifetimeHours = 2);
        var created = await app.CreateSessionAsync(lifetimeMinutes: 60);

        var extended = await app.Owner.ExtendAsync(created.Credentials, 60);
        Assert.Equal(app.Time.GetUtcNow().AddHours(2), extended.Value);

        var beyond = await app.Owner.ExtendAsync(created.Credentials, 15);
        Assert.Equal(SessionError.ExtensionLimitReached, beyond.Error);

        var unoffered = await app.Owner.ExtendAsync(created.Credentials, 37);
        Assert.Equal(SessionError.ValidationFailed, unoffered.Error);
    }

    [Fact]
    public async Task Removed_participant_loses_access_immediately()
    {
        using var app = new TestApp();
        var created = await app.CreateSessionAsync();
        var guest = (await app.JoinAsync(created.FormattedCode)).Credentials;

        Assert.True((await app.Owner.RemoveParticipantAsync(created.Credentials, guest.ParticipantId)).Succeeded);

        Assert.Equal(SessionError.Removed, (await app.Sync.UpdateAsync(guest, "x", 0, null)).Error);
        Assert.Contains(app.NotificationsOf<ParticipantRemovedNotification>(), n => n.ParticipantId == guest.ParticipantId);
    }

    [Fact]
    public async Task Rotating_the_code_invalidates_the_old_one_but_keeps_participants()
    {
        using var app = new TestApp();
        var created = await app.CreateSessionAsync();
        var guest = (await app.JoinAsync(created.FormattedCode)).Credentials;

        var rotated = await app.Owner.RotateCodeAsync(created.Credentials);

        Assert.True(rotated.Succeeded);
        Assert.NotEqual(created.FormattedCode, rotated.Value);
        var oldCode = await app.Sessions.JoinAsync(new JoinSessionCommand(created.FormattedCode, null, null, TestApp.ClientKey));
        Assert.Equal(SessionError.NotFoundOrPinIncorrect, oldCode.Error);
        await app.JoinAsync(rotated.Value!);
        Assert.True((await app.Sync.GetDocumentAsync(guest)).Succeeded);
    }

    [Fact]
    public async Task Pin_can_be_required_and_removed()
    {
        using var app = new TestApp();
        var created = await app.CreateSessionAsync();

        Assert.True((await app.Owner.SetPinAsync(created.Credentials, "2468")).Succeeded);
        var noPin = await app.Sessions.JoinAsync(new JoinSessionCommand(created.FormattedCode, null, null, TestApp.ClientKey));
        Assert.Equal(SessionError.NotFoundOrPinIncorrect, noPin.Error);
        await app.JoinAsync(created.FormattedCode, pin: "2468");

        Assert.True((await app.Owner.RemovePinAsync(created.Credentials)).Succeeded);
        await app.JoinAsync(created.FormattedCode);

        Assert.Equal(SessionError.ValidationFailed, (await app.Owner.SetPinAsync(created.Credentials, "12")).Error);
    }

    [Fact]
    public async Task Disabling_guest_editing_makes_guests_read_only_but_not_the_owner()
    {
        using var app = new TestApp();
        var created = await app.CreateSessionAsync();
        var guest = (await app.JoinAsync(created.FormattedCode)).Credentials;

        await app.Owner.SetGuestEditingAsync(created.Credentials, false);

        Assert.Equal(SessionError.ReadOnly, (await app.Sync.UpdateAsync(guest, "guest text", 0, null)).Error);
        Assert.True((await app.Sync.UpdateAsync(created.Credentials, "owner text", 0, null)).Succeeded);
        var snapshot = await app.Sessions.GetSnapshotAsync(guest);
        Assert.False(snapshot.Value!.CanEdit);
    }

    [Fact]
    public async Task Account_owner_can_reopen_and_close_from_my_sessions()
    {
        using var app = new TestApp();
        var created = await app.CreateSessionAsync(ownerUserId: "user-a");

        Assert.Equal(SessionError.NotFound, (await app.Sessions.OpenAsAccountOwnerAsync(created.PublicId, "user-b", null)).Error);
        var reopened = await app.Sessions.OpenAsAccountOwnerAsync(created.PublicId, "user-a", "Phone");
        Assert.True(reopened.Succeeded);
        Assert.True((await app.Sessions.GetSnapshotAsync(reopened.Value!)).Value!.IsOwner);

        Assert.Equal(SessionError.NotFound, (await app.Owner.CloseAsAccountOwnerAsync(created.PublicId, "user-b")).Error);
        Assert.True((await app.Owner.CloseAsAccountOwnerAsync(created.PublicId, "user-a")).Succeeded);
        Assert.Empty(await app.Sessions.ListOwnedAsync("user-a"));
    }
}
