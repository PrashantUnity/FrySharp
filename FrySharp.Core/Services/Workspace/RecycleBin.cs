using PdfEditorApp.Plugins.CSharpEditor.Services.Toolchains;

namespace PdfEditorApp.Plugins.CSharpEditor.Services.Workspace;

/// <summary>Moves files and folders to the operating system's Trash (Recycle Bin), where the user can put them back.</summary>
public interface IRecycleBin
{
    /// <summary>True when <paramref name="path"/> (a file or a folder) is now in the Trash; false when it couldn't be moved there.</summary>
    Task<bool> TryMoveToTrashAsync(string path, CancellationToken ct = default);
}

/// <summary>No Trash (tests, tools): nothing is ever moved there, so callers fall back to what they did without one.</summary>
public sealed class NoRecycleBin : IRecycleBin
{
    public static NoRecycleBin Instance { get; } = new();

    public Task<bool> TryMoveToTrashAsync(string path, CancellationToken ct = default) => Task.FromResult(false);
}

/// <summary>
/// The Trash of the machine: macOS's <c>/usr/bin/trash</c> (macOS 14 and later, with "Put Back"), else Finder; Windows' Recycle
/// Bin; on Linux the desktop's trash through <c>gio</c>. Every program runs off the calling thread, with a time limit.
/// </summary>
public sealed class RecycleBin : IRecycleBin
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private readonly IHostEnvironment _host;

    public RecycleBin(IHostEnvironment? host = null) => _host = host ?? new HostEnvironment();

    public async Task<bool> TryMoveToTrashAsync(string path, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        if (!Exists(full)) return false;

        try
        {
            if (OperatingSystem.IsMacOS())
            {
                if (_host.FileExists("/usr/bin/trash") &&
                    (await _host.RunAsync("/usr/bin/trash", [full], Limit, ct).ConfigureAwait(false)).ExitCode == 0 && !Exists(full))
                {
                    return true;
                }

                var escaped = full.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
                await _host.RunAsync("/usr/bin/osascript", ["-e", $"tell application \"Finder\" to delete POSIX file \"{escaped}\""], Limit, ct).ConfigureAwait(false);
                return !Exists(full);
            }

            if (OperatingSystem.IsWindows())
            {
                return await Task.Run(() =>
                {
                    if (Directory.Exists(full))
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(full, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    }
                    else
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(full, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    }

                    return !Exists(full);
                }, ct).ConfigureAwait(false);
            }

            if (OperatingSystem.IsLinux())
            {
                await _host.RunAsync("gio", ["trash", full], Limit, ct).ConfigureAwait(false);
                return !Exists(full);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            System.Diagnostics.Debug.WriteLine($"[RecycleBin] Couldn't move '{full}' to the Trash: {ex.Message}");
        }

        return false;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
