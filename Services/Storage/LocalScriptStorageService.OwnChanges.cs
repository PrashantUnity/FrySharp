using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using PdfEditorApp.Plugins.CSharpEditor.Services.Workspace;

namespace PdfEditorApp.Plugins.CSharpEditor.Services.Storage;

// The studio's own writes, and telling them apart from changes made by other programs.
//
// The file system reports the studio's writes back to it. Those echoes are dropped by path, for a few seconds after the
// write: a `git pull` in a terminal right after a save is still noticed. (They used to be dropped by time alone, every
// event for 30 s after any save, so whatever git or another editor changed in that window was never seen.)
public partial class LocalScriptStorageService
{
    private const long OwnChangeEchoMilliseconds = 5000;

    // Full path (a file, or a folder and everything below it) → when the studio changed it (Environment.TickCount64).
    private readonly ConcurrentDictionary<string, long> _ownChanges = new(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    // AtomicFile's temporary next to the file it saves: ".name.ext.<32 hex>.tmp". Never a change of the user's.
    [GeneratedRegex(@"^\..+\.[0-9a-f]{32}\.tmp$", RegexOptions.CultureInvariant)]
    private static partial Regex AtomicTemporaryName();

    /// <summary>Raised (on a background thread) with the files other programs changed; null when the system lost track and any file may have changed.</summary>
    public event Action<IReadOnlyCollection<string>?>? ExternalFilesChanged;

    /// <summary>Remembers that the studio itself is about to change (or just changed) <paramref name="paths"/>, so their echoes are not reported as outside changes.</summary>
    private void RememberOwnChange(params string?[] paths)
    {
        var now = Environment.TickCount64;
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            _ownChanges[TrimSeparators(Path.GetFullPath(path))] = now;
        }

        // Keep the table small: what is older than the echo window can go.
        if (_ownChanges.Count > 256)
        {
            foreach (var (key, tick) in _ownChanges)
            {
                if (now - tick > OwnChangeEchoMilliseconds) _ownChanges.TryRemove(key, out _);
            }
        }
    }

    private bool IsOwnEcho(string path)
    {
        if (AtomicTemporaryName().IsMatch(Path.GetFileName(path))) return true;

        var now = Environment.TickCount64;
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var candidate = TrimSeparators(path);
        foreach (var (own, tick) in _ownChanges)
        {
            if (now - tick > OwnChangeEchoMilliseconds) continue;
            if (candidate.Equals(own, comparison)) return true;
            if (candidate.Length > own.Length && candidate.StartsWith(own, comparison) &&
                (candidate[own.Length] == Path.DirectorySeparatorChar || candidate[own.Length] == Path.AltDirectorySeparatorChar))
            {
                return true;
            }
        }

        return false;
    }

    private static string TrimSeparators(string path) => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// Writes a document of the user's: atomically (a crash or a full disk leaves the old file or the new one, never half of
    /// one), and remembered as the studio's own change.
    /// </summary>
    private async Task WriteFileAsync(string path, string text, Encoding? encoding = null)
    {
        RememberOwnChange(path);
        await AtomicFile.WriteAllTextAsync(path, text, encoding ?? new UTF8Encoding(false)).ConfigureAwait(false);
        RememberOwnChange(path);
    }

    private void OnChangedOnDisk(WorkspaceChangeBatch batch)
    {
        List<string>? external = null;
        if (!batch.Overflowed)
        {
            external = batch.Paths.Where(p => !IsOwnEcho(p)).ToList();
            if (external.Count == 0) return;
        }

        BumpVersions(batch.Structural);
        ExternalChangeDetected?.Invoke();
        ExternalFilesChanged?.Invoke(external);
    }
}
