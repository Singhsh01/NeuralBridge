namespace NeuralBridge.Web.Components.Shared;

public enum ToastKind
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record Toast(Guid Id, string Message, ToastKind Kind);

/// <summary>Per-circuit toast queue. Toasts dismiss themselves after a few seconds and can be closed manually.</summary>
public sealed class ToastService
{
    private readonly List<Toast> _toasts = [];

    public event Action? Changed;

    public IReadOnlyList<Toast> Toasts => _toasts;

    public void Show(string message, ToastKind kind = ToastKind.Info, TimeSpan? duration = null)
    {
        var toast = new Toast(Guid.NewGuid(), message, kind);
        _toasts.Add(toast);
        if (_toasts.Count > 4)
        {
            _toasts.RemoveAt(0);
        }

        Changed?.Invoke();
        _ = DismissLaterAsync(toast.Id, duration ?? (kind == ToastKind.Error ? TimeSpan.FromSeconds(7) : TimeSpan.FromSeconds(4)));
    }

    public void Success(string message) => Show(message, ToastKind.Success);

    public void Error(string message) => Show(message, ToastKind.Error);

    public void Dismiss(Guid id)
    {
        if (_toasts.RemoveAll(t => t.Id == id) > 0)
        {
            Changed?.Invoke();
        }
    }

    private async Task DismissLaterAsync(Guid id, TimeSpan delay)
    {
        await Task.Delay(delay);
        Dismiss(id);
    }
}
