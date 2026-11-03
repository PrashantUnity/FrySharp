using System.Diagnostics;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.Common;

/// <summary>"Reveal in Finder" / "Reveal in File Explorer", the same from every Explorer: a file is shown selected in its folder.</summary>
public static class FileManagerReveal
{
    public static string Label => OperatingSystem.IsMacOS() ? "Reveal in Finder" : "Reveal in File Explorer";

    /// <summary>Opens the file manager at <paramref name="fullPath"/>, off the UI thread. False when there is nothing there.</summary>
    public static bool Reveal(string fullPath)
    {
        var isFile = File.Exists(fullPath);
        if (!isFile && !Directory.Exists(fullPath)) return false;

        _ = Task.Run(() =>
        {
            try
            {
                var start = new ProcessStartInfo { UseShellExecute = false };
                if (OperatingSystem.IsMacOS())
                {
                    start.FileName = "open";
                    if (isFile) start.ArgumentList.Add("-R"); // select it in its folder
                    start.ArgumentList.Add(fullPath);
                }
                else if (OperatingSystem.IsWindows())
                {
                    start.FileName = "explorer.exe";
                    start.ArgumentList.Add(isFile ? $"/select,{fullPath}" : fullPath);
                }
                else
                {
                    start.FileName = "xdg-open";
                    start.ArgumentList.Add(isFile ? Path.GetDirectoryName(fullPath) ?? fullPath : fullPath);
                }

                Process.Start(start)?.Dispose();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                Debug.WriteLine($"[FileManagerReveal] Couldn't reveal '{fullPath}': {ex.Message}");
            }
        });
        return true;
    }
}
