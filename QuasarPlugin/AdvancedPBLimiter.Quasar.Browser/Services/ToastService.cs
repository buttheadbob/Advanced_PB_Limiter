namespace AdvancedPBLimiter.Quasar.Services;

public enum ToastTone
{
    Info,
    Success,
    Warning,
    Danger
}

public sealed record PblToast(Guid Id, string Message, ToastTone Tone);

/// <summary>
/// The browser's small notification queue. It is deliberately static: the pages are separate components and the
/// toast host sits in the shared page frame, so one queue survives navigation within the dashboard.
/// </summary>
public static class ToastService
{
    private static readonly List<PblToast> s_toasts = new();

    public static event Action? Changed;

    public static IReadOnlyList<PblToast> Snapshot => s_toasts.ToArray();

    public static void Show(string message, ToastTone tone = ToastTone.Info, int seconds = 5)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        PblToast toast = new(Guid.NewGuid(), message, tone);
        s_toasts.Add(toast);
        Changed?.Invoke();
        _ = ExpireAsync(toast.Id, Math.Max(1, seconds));
    }

    public static void Dismiss(Guid id)
    {
        if (s_toasts.RemoveAll(toast => toast.Id == id) > 0)
            Changed?.Invoke();
    }

    public static void Clear()
    {
        if (s_toasts.Count == 0) return;
        s_toasts.Clear();
        Changed?.Invoke();
    }

    private static async Task ExpireAsync(Guid id, int seconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        Dismiss(id);
    }
}
