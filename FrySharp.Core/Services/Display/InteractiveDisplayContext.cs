namespace PdfEditorApp.Plugins.CSharpEditor.Services.Display;

/// <summary>
/// Cooperative-cancellation counterpart to InteractiveDisplayContext, using the same AsyncLocal
/// pattern: exposes the current execution's CancellationToken to user code via Display.CancellationToken/
/// ThrowIfCancellationRequested(), so a frame loop like
///   for (...) { Display.ThrowIfCancellationRequested(); Display.Image(...); await Task.Delay(16, Display.CancellationToken); }
/// can actually be stopped by the Stop button / timeout. Without this, user code has no way to observe
/// the host's cancellation token at all — Roslyn scripting only checks it between whole cell/script
/// submissions, never inside one, so an unchecked loop (or one using bare Task.Delay with no token)
/// runs to completion regardless of Stop/timeout.
/// </summary>
public static class InteractiveCancellationContext
{
    private static readonly AsyncLocal<CancellationToken> _current = new();

    public static CancellationToken Current => _current.Value;

    public static IDisposable EnterScope(CancellationToken token)
    {
        var previous = _current.Value;
        _current.Value = token;
        // Loops the studio instrumented look at this token once it is cancelled (see LoopGuard).
        var tracked = Execution.LoopGuard.Track(token);
        return new Scope(() =>
        {
            tracked.Dispose();
            _current.Value = previous;
        });
    }

    private sealed class Scope : IDisposable
    {
        private readonly Action _onDispose;
        private bool _disposed;
        public Scope(Action onDispose) => _onDispose = onDispose;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _onDispose();
        }
    }
}

/// <summary>
/// Disposes a live Display.Control(...) output when it's overwritten or discarded, so a
/// Display.Animate(...) control's DispatcherTimer actually stops instead of leaking forever on cell
/// re-run, "Clear Outputs", or tab close.
/// </summary>
public static class InteractiveControlLifecycle
{
    public static void DisposeIfNeeded(object? control)
    {
        if (control is IDisposable disposable)
        {
            try { disposable.Dispose(); }
            catch { /* best-effort teardown */ }
        }
    }
}

public static class InteractiveDisplayContext
{
    private static readonly AsyncLocal<Action<RichCellOutput>?> _currentOutputHandler = new();

    public static IDisposable EnterScope(Action<RichCellOutput> handler)
    {
        var prev = _currentOutputHandler.Value;
        _currentOutputHandler.Value = handler;
        return new Scope(() => _currentOutputHandler.Value = prev);
    }

    public static void Emit(RichCellOutput output)
    {
        _currentOutputHandler.Value?.Invoke(output);
    }

    private sealed class Scope : IDisposable
    {
        private readonly Action _onDispose;
        public Scope(Action onDispose) => _onDispose = onDispose;
        public void Dispose() => _onDispose();
    }
}
