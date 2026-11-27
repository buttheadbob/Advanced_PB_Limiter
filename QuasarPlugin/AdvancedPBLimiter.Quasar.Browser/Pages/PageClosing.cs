using Advanced_PB_Limiter.QuasarContracts;

namespace AdvancedPBLimiter.Quasar.Pages;

/// <summary>What the pages share.</summary>
internal static class PageShared
{
    /// <summary>
    /// The reasons a server's report could not be read, logged once per reason, and again only after 10 minutes, by the
    /// Live view and the report page alike, whoever has them open and however often they are reopened.
    /// </summary>
    public static readonly RecentReasons ReportReadFailures = new();

    /// <summary>Runs <paramref name="refresh"/> every <paramref name="interval"/> until the page closes.</summary>
    public static async Task RefreshEveryAsync(TimeSpan interval, CancellationToken closing, Func<Task> refresh)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(closing))
                await refresh();
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested)
        {
            // The page closed.
        }
    }

    /// <summary xml:lang="en">
    /// The part of an SDK failure worth showing an admin. The browser client appends its own JavaScript stack to the
    /// message, which belongs in the console, not in a notice; the companion's most common failure is said plainly.
    /// </summary>
    public static string ShortError(Exception error)
    {
        string message = error.Message ?? "";
        int stack = message.IndexOf(" at ", StringComparison.Ordinal);
        if (stack > 0) message = message[..stack];
        message = message.Replace("QuasarSdkError: ", "", StringComparison.Ordinal).Trim();

        if (message.Contains("Quasar SDK request failed", StringComparison.OrdinalIgnoreCase))
            return "The server did not answer. The companion plugin may not be loaded, or the server may still be starting.";

        return string.IsNullOrWhiteSpace(message) ? "The request failed." : message;
    }

    /// <summary>
    /// Runs <paramref name="render"/> unless the page has closed. A request that finishes after the page closed would
    /// otherwise call into a renderer that is gone, and that failure would end the fire-and-forget loop that refreshes
    /// the page. The page passes its own <c language="csharp">InvokeAsync(StateHasChanged)</c>, which only it may call.
    /// </summary>
    public static async Task RenderIfOpenAsync(PageClosing closing, Func<Task> render)
    {
        if (closing.IsClosed) return;
        try
        {
            await render();
        }
        catch (ObjectDisposedException)
        {
            // Closed between the check and the call.
        }
        catch (InvalidOperationException)
        {
            // The renderer is disposed: the same, seen from the dispatcher.
        }
    }
}

/// <summary>Cancelled when a page closes, so its timers and requests stop. Disposed with the page.</summary>
internal sealed class PageClosing : IDisposable
{
    private readonly CancellationTokenSource _source = new();

    /// <summary>
    /// The page's token. Disposed only after it is cancelled, and a disposed source's Token throws, while an await can
    /// resume after Dispose: a closed page gets an already cancelled token instead.
    /// </summary>
    public CancellationToken Token => _source.IsCancellationRequested ? new CancellationToken(true) : _source.Token;

    public bool IsClosed => _source.IsCancellationRequested;

    public void Dispose()
    {
        _source.Cancel();
        _source.Dispose();
    }
}

internal static class CompanionErrors
{
    /// <summary>
    /// A server started before this plugin was enabled has no companion until it restarts. The worker adapter's message
    /// keeps Quasar's own wording, so the page can still tell that restart apart.
    /// </summary>
    public static string RestartHint(Exception e) =>
        e.Message.Contains("is not loaded or does not expose a Quasar handler", StringComparison.Ordinal)
            ? " If the server was started before this plugin was enabled, it needs a restart."
            : "";

    /// <summary>
    /// A server's or Quasar's error message, which starts as a sentence of its own, for the middle of one: its first letter
    /// in lower case, unless the first word looks like a name or an acronym (a second capital).
    /// </summary>
    public static string InSentence(string message) =>
        message.Length > 1 && char.IsUpper(message[0]) && !char.IsUpper(message[1])
            ? char.ToLowerInvariant(message[0]) + message.Substring(1)
            : message;
}
