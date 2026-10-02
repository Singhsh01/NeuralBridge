using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using NeuralBridge.Application.Calls;
using NeuralBridge.Application.Common;
using NeuralBridge.Application.Documents;
using NeuralBridge.Application.Realtime;
using NeuralBridge.Application.Sessions;
using NeuralBridge.Application.Snippets;
using NeuralBridge.Domain.Sessions;
using NeuralBridge.Web.Auth;
using NeuralBridge.Web.Components.Session;
using NeuralBridge.Web.Components.Shared;

namespace NeuralBridge.Web.Components.Pages;

public enum WorkspaceView
{
    Loading,
    NeedsJoin,
    Ready,
    Ended,
    Removed,
    Failed,
}

/// <summary>
/// Orchestrates one participant's view of a session: it authorizes stored credentials,
/// subscribes to the session's notifications, wires the editor to the sync service, and
/// switches to an explanatory state when access ends. Business rules live in the
/// application services. This component only translates between them and the UI.
/// </summary>
public sealed partial class Workspace : IAsyncDisposable
{
    private static readonly TimeSpan TypingIndicatorDuration = TimeSpan.FromSeconds(3);

    private readonly string _originId = $"circuit-{Guid.NewGuid():N}";
    private readonly CancellationTokenSource _disposed = new();

    private WorkspaceView _view = WorkspaceView.Loading;
    private ParticipantCredentials? _credentials;
    private Guid _sessionId;
    private SessionSnapshot? _snapshot;
    private DocumentView? _document;
    private SharedEditor? _editor;
    private CallPanel? _callPanel;
    private IReadOnlyList<CallRosterEntry> _callRoster = [];
    private bool _confirmExit;
    private IDisposable? _subscription;
    private ITimer? _expiryTimer;
    private SessionEndReason _endReason;
    private string? _failureMessage;
    private string? _typingName;
    private DateTimeOffset _typingUntil;
    private DateTimeOffset? _lastSyncedAt;
    private string? _lastEditor;
    private IReadOnlyList<Uri> _links = [];
    private int _maxLength;
    private bool _canSaveToAccount;
    private string? _userId;
    private bool _saving;
    private bool _confirmClear;
    private bool _confirmRemove;
    private ParticipantView? _removeCandidate;

    [Parameter] public string PublicId { get; set; } = string.Empty;

    [Inject] private SessionCredentialStore CredentialStore { get; set; } = default!;

    [Inject] private ISessionService Sessions { get; set; } = default!;

    [Inject] private ISessionAuthorizationService Authorization { get; set; } = default!;

    [Inject] private ITextSynchronizationService Sync { get; set; } = default!;

    [Inject] private ISessionOwnerService Owner { get; set; } = default!;

    [Inject] private ISessionCleanupService Cleanup { get; set; } = default!;

    [Inject] private ISnippetService Snippets { get; set; } = default!;

    [Inject] private ISessionEventBus Bus { get; set; } = default!;

    [Inject] private WorkspacePresence Presence { get; set; } = default!;

    [Inject] private ToastService Toasts { get; set; } = default!;

    [Inject] private NavigationManager Navigation { get; set; } = default!;

    [Inject] private AuthenticationStateProvider AuthenticationState { get; set; } = default!;

    [Inject] private AuthProviderStatus Accounts { get; set; } = default!;

    [Inject] private TimeProvider Time { get; set; } = default!;

    [Inject] private IServiceProvider Services { get; set; } = default!;

    [Inject] private ILogger<Workspace> Logger { get; set; } = default!;

    private string ShareUrl => $"{Navigation.BaseUri}join/{_snapshot?.FormattedCode}";

    private string EndedIcon => _endReason == SessionEndReason.Expired ? "hourglass" : "door-open";

    private string EndedTitle => _endReason == SessionEndReason.Expired ? "This session has expired" : "This session was closed";

    private string EndedMessage => _endReason == SessionEndReason.Expired
        ? "Its time ran out, so every device was disconnected and the shared text was deleted from the server."
        : "The owner closed it, so every device was disconnected and the shared text was deleted from the server.";

    protected override void OnInitialized() =>
        _maxLength = NeuralBridge.Application.DependencyInjection.GetSessionOptions(Services).MaxContentLength;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await OpenAsync();
        }
    }

    private async Task OpenAsync()
    {
        try
        {
            _userId = await GetUserIdAsync();
            _credentials = await CredentialStore.GetAsync(PublicId);

            if (_credentials is null && _userId is not null && await Sessions.IsAccountOwnerAsync(PublicId, _userId, _disposed.Token))
            {
                var reopened = await Sessions.OpenAsAccountOwnerAsync(PublicId, _userId, null, _disposed.Token);
                if (reopened.Succeeded)
                {
                    _credentials = reopened.Value!;
                    await CredentialStore.SaveAsync(_credentials);
                }
            }

            if (_credentials is null)
            {
                SetView(WorkspaceView.NeedsJoin);
                return;
            }

            var auth = await Authorization.AuthorizeAsync(_credentials, _disposed.Token);
            if (!auth.Succeeded)
            {
                await HandleAccessLostAsync(auth.Error);
                return;
            }

            _sessionId = auth.Value!.SessionId;
            _subscription?.Dispose();
            _subscription = Bus.Subscribe(_sessionId, OnNotificationAsync);
            await Presence.EnterAsync(_sessionId, _credentials.ParticipantId, _originId);

            var document = await Sync.GetDocumentAsync(_credentials, _disposed.Token);
            if (!document.Succeeded)
            {
                await HandleAccessLostAsync(document.Error);
                return;
            }

            _document = document.Value!;
            _links = LinkDetector.Find(_document.Content);
            _lastEditor = _document.LastEditorName;
            _canSaveToAccount = Accounts.AccountsAvailable && _userId is not null;
            if (!await RefreshSnapshotCoreAsync())
            {
                return;
            }

            SetView(WorkspaceView.Ready);
        }
        catch (OperationCanceledException) when (_disposed.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to open workspace");
            _failureMessage = "Something interrupted the connection to the session. Your text is still on the server while the session runs.";
            SetView(WorkspaceView.Failed);
        }
    }

    private Task RetryAsync()
    {
        _view = WorkspaceView.Loading;
        return OpenAsync();
    }

    private async Task RefreshSnapshotAsync()
    {
        if (await RefreshSnapshotCoreAsync())
        {
            StateHasChanged();
        }
    }

    /// <returns><c>false</c> when access was lost (the view has already switched).</returns>
    private async Task<bool> RefreshSnapshotCoreAsync()
    {
        if (_credentials is null)
        {
            return false;
        }

        var snapshot = await Sessions.GetSnapshotAsync(_credentials, _disposed.Token);
        if (!snapshot.Succeeded)
        {
            await HandleAccessLostAsync(snapshot.Error);
            return false;
        }

        _snapshot = snapshot.Value!;
        ScheduleExpiryCheck(_snapshot.ExpiresAt);
        return true;
    }

    private async Task<EditAck> HandleLocalEditAsync(string text, long baseVersion)
    {
        if (_credentials is null || _view != WorkspaceView.Ready)
        {
            return EditAck.Rejected();
        }

        var result = await Sync.UpdateAsync(_credentials, text, baseVersion, _originId, _disposed.Token);
        if (result.Succeeded)
        {
            if (result.Value!.Changed)
            {
                _lastSyncedAt = Time.GetUtcNow();
                _lastEditor = null;
                _links = LinkDetector.Find(text);
                await InvokeAsync(StateHasChanged);
            }

            return EditAck.Accepted(result.Value.Version);
        }

        switch (result.Error)
        {
            case SessionError.RateLimited:
                return EditAck.RetryLater();
            case SessionError.ReadOnly:
                Toasts.Show("The owner turned off editing for guests. Your last change wasn't shared.", ToastKind.Warning);
                await InvokeAsync(RefreshSnapshotAsync);
                break;
            case SessionError.ValidationFailed:
                Toasts.Error(result.Message ?? ErrorMessages.For(result.Error));
                break;
            default:
                await InvokeAsync(() => HandleAccessLostAsync(result.Error));
                break;
        }

        return EditAck.Rejected();
    }

    private async Task HandleTypingAsync()
    {
        if (_credentials is not null && _view == WorkspaceView.Ready)
        {
            await Sync.NotifyTypingAsync(_credentials, _originId, _disposed.Token);
        }
    }

    private Task OnNotificationAsync(SessionNotification notification) =>
        InvokeAsync(async () =>
        {
            if (_view != WorkspaceView.Ready || _disposed.IsCancellationRequested)
            {
                return;
            }

            switch (notification)
            {
                case DocumentChangedNotification changed when changed.OriginId != _originId:
                    if (_editor is not null)
                    {
                        await _editor.ApplyRemoteAsync(changed.Content, changed.Version);
                    }

                    _lastSyncedAt = changed.UpdatedAt;
                    _lastEditor = changed.EditorParticipantId == _credentials?.ParticipantId ? null : changed.EditorName;
                    _links = LinkDetector.Find(changed.Content);
                    _typingName = null;
                    StateHasChanged();
                    break;

                case TypingNotification typing when typing.OriginId != _originId && typing.ParticipantId != _credentials?.ParticipantId:
                    _typingName = typing.DisplayName;
                    _typingUntil = Time.GetUtcNow() + TypingIndicatorDuration;
                    StateHasChanged();
                    _ = ClearTypingLaterAsync();
                    break;

                case ParticipantRemovedNotification removed when removed.ParticipantId == _credentials?.ParticipantId:
                    await HandleAccessLostAsync(SessionError.Removed);
                    break;

                case ParticipantsChangedNotification or SessionSettingsChangedNotification or ParticipantRemovedNotification:
                    await RefreshSnapshotAsync();
                    break;

                case SessionEndedNotification ended:
                    _endReason = ended.Reason;
                    await EndAsync(WorkspaceView.Ended);
                    break;

                case CallSignalNotification signal when signal.ToParticipantId == _credentials?.ParticipantId && _callPanel is not null:
                    await _callPanel.DeliverSignalAsync(signal.FromParticipantId, signal.Payload);
                    break;

                case CallStateChangedNotification when _callPanel is not null:
                    await _callPanel.RefreshRosterAsync();
                    break;
            }
        });

    private void OnCallRosterChanged(IReadOnlyList<CallRosterEntry> roster) => _callRoster = roster;

    private async Task ClearTypingLaterAsync()
    {
        try
        {
            await Task.Delay(TypingIndicatorDuration + TimeSpan.FromMilliseconds(100), _disposed.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await InvokeAsync(() =>
        {
            if (_typingName is not null && Time.GetUtcNow() >= _typingUntil)
            {
                _typingName = null;
                StateHasChanged();
            }
        });
    }

    private async Task ClearAsync()
    {
        if (_credentials is null)
        {
            return;
        }

        var result = await Sync.ClearAsync(_credentials, _originId, _disposed.Token);
        if (result.Succeeded)
        {
            if (_editor is not null)
            {
                await _editor.SetContentAsync(string.Empty, result.Value!.Version);
            }

            _links = [];
            _lastSyncedAt = Time.GetUtcNow();
            Toasts.Success("Text cleared on every device.");
        }
        else if (result.Error is SessionError.ReadOnly or SessionError.ValidationFailed or SessionError.RateLimited)
        {
            Toasts.Error(result.Message ?? ErrorMessages.For(result.Error));
        }
        else
        {
            await HandleAccessLostAsync(result.Error);
        }
    }

    private async Task SaveToAccountAsync()
    {
        if (_credentials is null || _userId is null)
        {
            return;
        }

        _saving = true;
        try
        {
            var saved = await Snippets.SaveFromSessionAsync(_userId, _credentials, null, _disposed.Token);
            if (saved.Succeeded)
            {
                Toasts.Success($"Saved \"{saved.Value!.Title}\" to your account.");
            }
            else
            {
                Toasts.Error(saved.Message ?? ErrorMessages.For(saved.Error));
            }
        }
        finally
        {
            _saving = false;
        }
    }

    private void RequestRemove(ParticipantView participant)
    {
        _removeCandidate = participant;
        _confirmRemove = true;
    }

    private async Task RemoveParticipantAsync()
    {
        if (_credentials is null || _removeCandidate is null)
        {
            return;
        }

        var name = _removeCandidate.DisplayName;
        var result = await Owner.RemoveParticipantAsync(_credentials, _removeCandidate.Id, _disposed.Token);
        if (result.Succeeded)
        {
            Toasts.Success($"Removed {name}.");
        }
        else
        {
            Toasts.Error(result.Message ?? ErrorMessages.For(result.Error));
        }

        _removeCandidate = null;
    }

    private async Task LeaveAsync()
    {
        if (_credentials is not null)
        {
            await Sessions.LeaveAsync(_credentials, _disposed.Token);
            await CredentialStore.RemoveAsync(PublicId);
        }

        await TearDownAsync();
        Navigation.NavigateTo("/");
    }

    /// <summary>Owner exits but the session keeps running; credentials stay in this tab so they can come back.</summary>
    private async Task ExitKeepRunningAsync()
    {
        _confirmExit = false;
        await TearDownAsync();
        Navigation.NavigateTo("/");
    }

    private async Task CloseFromExitAsync()
    {
        _confirmExit = false;
        if (_credentials is null)
        {
            return;
        }

        var result = await Owner.CloseAsync(_credentials, _disposed.Token);
        if (!result.Succeeded)
        {
            Toasts.Error(result.Message ?? ErrorMessages.For(result.Error));
        }
    }

    private async Task HandleAccessLostAsync(SessionError error)
    {
        switch (error)
        {
            case SessionError.Expired:
                _endReason = SessionEndReason.Expired;
                await EndAsync(WorkspaceView.Ended);
                break;
            case SessionError.Closed:
                _endReason = SessionEndReason.ClosedByOwner;
                await EndAsync(WorkspaceView.Ended);
                break;
            case SessionError.Removed:
                await EndAsync(WorkspaceView.Removed);
                break;
            case SessionError.Unauthorized:
                await CredentialStore.RemoveAsync(PublicId);
                await TearDownAsync();
                SetView(WorkspaceView.NeedsJoin);
                break;
            default:
                _failureMessage = ErrorMessages.For(error);
                await TearDownAsync();
                SetView(WorkspaceView.Failed);
                break;
        }
    }

    private async Task EndAsync(WorkspaceView view)
    {
        if (_callPanel is not null)
        {
            await _callPanel.HangUpLocallyAsync();
        }

        await CredentialStore.RemoveAsync(PublicId);
        await TearDownAsync();

        // The editor (and its local copy of the text) leaves the DOM with the Ready view.
        _document = null;
        _snapshot = null;
        _links = [];
        SetView(view);
    }

    private void SetView(WorkspaceView view)
    {
        _view = view;
        StateHasChanged();
    }

    private void ScheduleExpiryCheck(DateTimeOffset expiresAt)
    {
        _expiryTimer?.Dispose();
        var due = expiresAt - Time.GetUtcNow() + TimeSpan.FromMilliseconds(500);
        if (due < TimeSpan.Zero)
        {
            due = TimeSpan.Zero;
        }

        // Enforcement doesn't depend on this timer (the sweeper and lazy checks also expire
        // sessions). It only makes the "expired" screen appear on time.
        var sessionId = _sessionId;
        _expiryTimer = Time.CreateTimer(
            _ => _ = Cleanup.ExpireIfDueAsync(sessionId),
            null,
            due,
            Timeout.InfiniteTimeSpan);
    }

    private async Task TearDownAsync()
    {
        _subscription?.Dispose();
        _subscription = null;
        _expiryTimer?.Dispose();
        _expiryTimer = null;
        await Presence.LeaveAsync();
    }

    private async Task<string?> GetUserIdAsync()
    {
        if (!Accounts.AccountsAvailable)
        {
            return null;
        }

        var state = await AuthenticationState.GetAuthenticationStateAsync();
        return state.User.Identity?.IsAuthenticated == true ? state.User.FindFirstValue(ClaimTypes.NameIdentifier) : null;
    }

    public async ValueTask DisposeAsync()
    {
        // Cancel (not dispose): late callbacks may still read the token safely.
        await _disposed.CancelAsync();
        await TearDownAsync();
    }
}
