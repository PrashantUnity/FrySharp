using System.Diagnostics;

namespace PdfEditorApp.Plugins.CSharpEditor.Services.Workspace;

/// <summary>
/// One settled burst of changes: the full paths it touched, whether anything was created, deleted or renamed, and whether the
/// operating system lost track (<see cref="Overflowed"/>: anything may have changed, <see cref="Paths"/> is incomplete).
/// </summary>
public sealed record WorkspaceChangeBatch(bool Structural, IReadOnlyCollection<string> Paths, bool Overflowed);

/// <summary>
/// Watches a workspace folder and reports, once things have settled, that files under it changed behind the studio's back
/// (git, another editor, a build). Without it the studio would only notice on a rescan, and rescanning the whole workspace
/// on every tab switch is exactly what it no longer does.
/// </summary>
public sealed class WorkspaceWatcher : IDisposable
{
    // A git checkout or a build writes hundreds of files in a burst: report once, after it has gone quiet.
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(300);

    private readonly string _root;
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _settleTimer;
    private readonly Action<WorkspaceChangeBatch> _changed;
    private int _structural;

    // The paths of the burst being collected. A burst of thousands (a checkout) stops collecting and counts as overflowed:
    // listing them all would cost more than treating everything as changed.
    private const int MaxPathsPerBatch = 2000;
    private readonly object _pathsGate = new();
    private HashSet<string> _paths = new(StringComparer.Ordinal);
    private bool _overflowed;

    private WorkspaceWatcher(string root, FileSystemWatcher watcher, Action<WorkspaceChangeBatch> changed)
    {
        _root = root;
        _watcher = watcher;
        _changed = changed;
        _settleTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);

        watcher.Created += (_, e) => Report(e.FullPath, structural: true);
        watcher.Deleted += (_, e) => Report(e.FullPath, structural: true);
        watcher.Renamed += (_, e) =>
        {
            Report(e.OldFullPath, structural: true);
            Report(e.FullPath, structural: true);
        };
        watcher.Changed += (_, e) => Report(e.FullPath, structural: false);
        // Events were lost (the operating system's buffer overflowed): anything may have changed.
        watcher.Error += (_, _) => Report(null, structural: true);
    }

    /// <param name="changed">Called on a background thread after a burst of changes; the argument is true when something was created, deleted or renamed.</param>
    /// <returns>The watcher, or null when the folder can't be watched (it is gone, or the platform's watch limit is reached): the workspace then refreshes only when asked.</returns>
    public static WorkspaceWatcher? TryStart(string root, Action<bool> changed) => TryStart(root, batch => changed(batch.Structural));

    /// <param name="changed">Called on a background thread after a burst of changes, with the paths it touched.</param>
    /// <returns>The watcher, or null when the folder can't be watched (it is gone, or the platform's watch limit is reached): the workspace then refreshes only when asked.</returns>
    public static WorkspaceWatcher? TryStart(string root, Action<WorkspaceChangeBatch> changed)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            var started = new WorkspaceWatcher(root, watcher, changed);
            watcher.EnableRaisingEvents = true;
            return started;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException or InvalidOperationException)
        {
            Debug.WriteLine($"[CSharpEditorPlugin] Not watching '{root}' for changes: {ex.Message}");
            watcher?.Dispose();
            return null;
        }
    }

    private void Report(string? path, bool structural)
    {
        // Inside the folders the walk leaves out (.git, node_modules...) things change constantly; the Explorer lists them
        // only when opened (as VS Code's watcher, which ignores them too).
        if (path != null && WorkspaceWalker.IsInsideSkippedFolder(path, _root)) return;

        if (structural) Interlocked.Exchange(ref _structural, 1);
        lock (_pathsGate)
        {
            if (path == null || _paths.Count >= MaxPathsPerBatch) _overflowed = true;
            else _paths.Add(path);
        }

        try
        {
            _settleTimer.Change(Settle, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Stopped while an event was arriving.
        }
    }

    private void Flush()
    {
        HashSet<string> paths;
        bool overflowed;
        lock (_pathsGate)
        {
            paths = _paths;
            overflowed = _overflowed;
            _paths = new HashSet<string>(StringComparer.Ordinal);
            _overflowed = false;
        }

        _changed(new WorkspaceChangeBatch(Interlocked.Exchange(ref _structural, 0) == 1, paths, overflowed));
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _settleTimer.Dispose();
    }
}
