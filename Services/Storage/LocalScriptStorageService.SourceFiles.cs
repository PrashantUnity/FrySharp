using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Services.Languages;
using PdfEditorApp.Plugins.CSharpEditor.Services.Workspace;

namespace PdfEditorApp.Plugins.CSharpEditor.Services.Storage;

/// <summary>
/// Plain source files (<c>main.py</c>): listed with the workspace's documents, opened and saved as their text so other
/// tools keep working on them, and never overwritten behind the user's back. A document's id is derived from its path,
/// so the same file has the same id in every session.
/// </summary>
public partial class LocalScriptStorageService
{
    private const string SourceFileIdPrefix = "src-";

    private readonly LanguageRegistry _languages;
    private readonly object _sourceGate = new();
    private readonly Dictionary<string, SourceFileState> _sourceFiles = new(StringComparer.OrdinalIgnoreCase);

    // What the file held when it was last read or written here: an implicit save only writes over that.
    private sealed class SourceFileState
    {
        public required string Path { get; set; }
        public required string LastSavedText { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
        public bool HasByteOrderMark { get; set; }

        /// <summary>
        /// The editor shows a description of the file, not its content (a binary file, or one too large to open). Saving
        /// it would write that description over the real file, so it is never saved.
        /// </summary>
        public bool IsPlaceholder { get; set; }
    }

    /// <summary>Text files larger than this are not loaded into the editor (as in VS Code); a description opens instead.</summary>
    public const long MaxEditorFileBytes = 50L * 1024 * 1024;

    // Enough of a file to tell text from binary without reading the rest of it.
    private const int BinarySniffBytes = 8192;

    public LanguageRegistry Languages => _languages;

    public static bool IsSourceFileId(string? id) => ScriptStorageIdHelper.IsSourceFileId(id);

    /// <summary>The id of the source file at <paramref name="path"/>: the same path always gives the same id.</summary>
    public static string SourceFileId(string path) => ScriptStorageIdHelper.SourceFileId(path);

    private static readonly HashSet<string> IgnoredFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store",
        "Thumbs.db",
        "desktop.ini",
        "fry_display.zig",
        "fry_display.dart",
        "fry_display.py",
        "fry_display.js",
        "fry_display.hpp",
        "fry_display.cs"
    };

    private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".frycsproj",
        ".frynbproj"
    };

    private static readonly HashSet<string> KnownBinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".tiff", ".tif",
        ".pdf", ".exe", ".dll", ".so", ".dylib", ".bin", ".dat", ".zip", ".tar",
        ".gz", ".7z", ".rar", ".mp3", ".wav", ".mp4", ".mov", ".avi", ".mkv"
    };

    private static bool IsIgnoredFile(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name) || IgnoredFileNames.Contains(name)) return true;
        if (name.StartsWith("fry_display.", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("fry_channel.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        var ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && IgnoredExtensions.Contains(ext);
    }

    private bool IsWorkspaceFile(string path) => !IsIgnoredFile(path);

    private static async Task<bool> StartsLikeBinaryAsync(string path)
    {
        var buffer = new byte[BinarySniffBytes];
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BinarySniffBytes, useAsync: true);
        int read = await fs.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false);
        return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
    }

    private string GenerateLargeFileNote(string path, long fileSize)
    {
        var fileName = Path.GetFileName(path);
        var rel = GetFolderPath(path);
        var relativePath = string.IsNullOrEmpty(rel) ? fileName : $"{rel}/{fileName}";
        return $$"""
            // {{fileName}} is {{FormatFileSize(fileSize)}}: too large to open in the editor (the limit is {{FormatFileSize(MaxEditorFileBytes)}}).
            // It is left untouched: this note is never saved over it.
            //
            // Read it from code instead, a part at a time:
            //   foreach (var line in System.IO.File.ReadLines("{{relativePath}}").Take(100)) Console.WriteLine(line);
            """;
    }

    private static bool IsBinaryFile(string path, byte[] bytes)
    {
        var ext = Path.GetExtension(path);
        if (KnownBinaryExtensions.Contains(ext)) return true;
        var checkLen = Math.Min(bytes.Length, 8192);
        return Array.IndexOf(bytes, (byte)0, 0, checkLen) >= 0;
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F2} MB";
    }

    private string GenerateBinaryFileSnippet(string path, long fileSize)
    {
        var fileName = Path.GetFileName(path);
        var rel = GetFolderPath(path);
        var relativePath = string.IsNullOrEmpty(rel) ? fileName : $"{rel}/{fileName}";
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var sizeText = FormatFileSize(fileSize);
        var isImage = ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".ico" or ".webp" or ".svg";

        if (isImage)
        {
            return $$"""
                // ==============================================================================
                //  Image Asset: {{fileName}} ({{sizeText}})
                //  Relative Path: {{relativePath}}
                //  Full Path: {{path}}
                // ==============================================================================
                //
                // [C# / SkiaSharp]
                // using SkiaSharp;
                // using var bitmap = SKBitmap.Decode("{{relativePath}}");
                //
                // [C# / Raw File Bytes]
                // byte[] bytes = System.IO.File.ReadAllBytes("{{relativePath}}");
                //
                // [C# / Avalonia Bitmap]
                // using var stream = System.IO.File.OpenRead("{{relativePath}}");
                // var avaloniaBitmap = new Avalonia.Media.Imaging.Bitmap(stream);
                //
                // [Python / PIL (Pillow)]
                // from PIL import Image
                // img = Image.open("{{relativePath}}")
                // ==============================================================================
                """;
        }

        return $$"""
            // ==============================================================================
            //  Binary Asset: {{fileName}} ({{sizeText}})
            //  Relative Path: {{relativePath}}
            //  Full Path: {{path}}
            // ==============================================================================
            //
            // [C# / Read All Bytes]
            // byte[] bytes = System.IO.File.ReadAllBytes("{{relativePath}}");
            //
            // [Python / Read Binary File]
            // with open("{{relativePath}}", "rb") as f:
            //     data = f.read()
            // ==============================================================================
            """;
    }

    private string RegisterSourceFile(string path)
    {
        var full = Path.GetFullPath(path);
        var id = SourceFileId(full);
        _knownFileLocations[id] = full;
        return id;
    }

    private WorkspaceItemSummary SourceFileSummary(string file, ILanguageDefinition language, string root) => new()
    {
        Id = RegisterSourceFile(file),
        Title = Path.GetFileNameWithoutExtension(file),
        Description = $"{language.DisplayName} file",
        Category = language.DisplayName,
        Kind = WorkspaceItemKind.Script,
        LanguageId = language.Id,
        LanguageName = language.DisplayName,
        FileExtension = Path.GetExtension(file),
        IsSourceFile = true,
        LastModified = LastWriteTimeUtc(file),
        FolderPath = GetFolderPath(file),
        FileName = Path.GetFileName(file),
        IsExternalRoot = IsExternalWorkspaceActive,
        WorkspaceRootName = IsExternalWorkspaceActive ? Path.GetFileName(root.TrimEnd('/', '\\')) : null
    };

    private async Task<string?> FindSourceFilePathAsync(string id)
    {
        if (_knownFileLocations.TryGetValue(id, out var known) && File.Exists(known)) return known;

        var root = EffectiveWorkspaceRoot;

        // The file index already holds every path of the workspace: look there rather than walking the folder again.
        var index = _fileIndex;
        if (!index.IsBuilding && !index.IsTruncated && string.Equals(index.Root, root, StringComparison.Ordinal) && index.Count > 0)
        {
            var found = await Task.Run(() =>
            {
                foreach (var relative in index.Paths)
                {
                    var full = Path.Combine(root, relative);
                    if (SourceFileId(Path.GetFullPath(full)) == id) return RegisterSourceFile(full);
                }

                return null;
            }).ConfigureAwait(false);
            if (found != null && _knownFileLocations.TryGetValue(found, out var located)) return located;
        }

        var files = await Task.Run(() => WorkspaceWalker.Files(root, IsWorkspaceFile)).ConfigureAwait(false);
        foreach (var file in files)
        {
            if (RegisterSourceFile(file) == id) return _knownFileLocations[id];
        }

        return null;
    }

    private async Task<ScriptDocumentItem?> LoadSourceFileAsync(string id)
    {
        var path = await FindSourceFilePathAsync(id);
        if (path == null) return null;
        var language = _languages.FindSourceFileLanguage(path) ?? _languages.Get(LanguageIds.Text);
        if (language == null) return null;

        try
        {
            // Size and a sniff of the first bytes decide first: a 2 GB video or log is never read into memory to find out.
            var info = new FileInfo(path);
            long size = info.Length;
            bool tooLarge = size > MaxEditorFileBytes;
            bool isBinary = KnownBinaryExtensions.Contains(Path.GetExtension(path)) || (!tooLarge && await StartsLikeBinaryAsync(path));

            string text;
            bool hasBom = false;

            if (isBinary)
            {
                text = GenerateBinaryFileSnippet(path, size);
            }
            else if (tooLarge)
            {
                text = GenerateLargeFileNote(path, size);
            }
            else
            {
                var bytes = await File.ReadAllBytesAsync(path);
                hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
                text = new UTF8Encoding(false).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
            }

            bool placeholder = isBinary || tooLarge;
            lock (_sourceGate)
            {
                _sourceFiles[id] = new SourceFileState
                {
                    Path = path,
                    LastSavedText = text,
                    LastWriteTimeUtc = info.LastWriteTimeUtc,
                    HasByteOrderMark = hasBom,
                    IsPlaceholder = placeholder
                };
            }

            return new ScriptDocumentItem
            {
                Id = id,
                Title = Path.GetFileName(path),
                Description = isBinary ? $"Binary asset ({FormatFileSize(size)})" : tooLarge ? $"Large file ({FormatFileSize(size)})" : $"{language.DisplayName} file",
                Category = isBinary ? "Assets" : language.DisplayName,
                Code = text,
                Notes = isBinary ? "[Binary File - Read Only Preview & Script References]" : tooLarge ? "[Large File - Not Opened in the Editor]" : string.Empty,
                LanguageId = language.Id,
                SourceFilePath = path,
                Created = info.CreationTimeUtc,
                LastModified = info.LastWriteTimeUtc
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[CSharpEditorPlugin] Failed to read '{path}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Writes a source file's text. Ctrl+S and Run pass <paramref name="overwriteChangesOnDisk"/>; the saves the studio
    /// makes on its own (switching files, leaving the studio) don't, and then write only when the text changed and
    /// nothing else has changed the file since the studio read it, so another editor's work is never lost.
    /// </summary>
    public async Task<bool> SaveSourceFileAsync(ScriptDocumentItem document, bool overwriteChangesOnDisk)
    {
        var path = document.SourceFilePath ?? await FindSourceFilePathAsync(document.Id);
        if (path == null) return false;

        if (document.IsReadOnly || document.Id.StartsWith("git-diff:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        SourceFileState? state;
        lock (_sourceGate) _sourceFiles.TryGetValue(document.Id, out state);

        // The editor holds a description of this file, not its content: writing it would destroy the file.
        if (IsBinaryFile(path, Array.Empty<byte>()) || state?.IsPlaceholder == true)
        {
            Debug.WriteLine($"[CSharpEditorPlugin] Skipping save on '{path}': the editor shows a placeholder, not the file's content.");
            return true;
        }
        var text = document.Code ?? string.Empty;

        try
        {
            if (!overwriteChangesOnDisk && state != null)
            {
                if (text == state.LastSavedText) return true;
                if (File.Exists(path) && File.GetLastWriteTimeUtc(path) != state.LastWriteTimeUtc)
                {
                    Debug.WriteLine($"[CSharpEditorPlugin] Not saving '{path}': it changed on disk since it was opened.");
                    return false;
                }
            }

            var hasBom = state?.HasByteOrderMark == true;
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(hasBom));
            var written = File.GetLastWriteTimeUtc(path);
            lock (_sourceGate)
            {
                _sourceFiles[document.Id] = new SourceFileState
                {
                    Path = path,
                    LastSavedText = text,
                    LastWriteTimeUtc = written,
                    HasByteOrderMark = hasBom
                };
            }

            document.SourceFilePath = path;
            document.LastModified = written;
            MarkChanged(structural: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[CSharpEditorPlugin] Failed to save '{path}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Creates a source file of a language with <paramref name="initialContent"/> in it (null: the language's new-file template).</summary>
    public async Task<ScriptDocumentItem?> CreateNewSourceFileAsync(string languageId, string? fileName = null, string? folderPath = null, string? initialContent = null)
    {
        await EnsureInitializedAsync();
        var language = _languages.Get(languageId);
        if (language == null || language.Storage != LanguageStorageKind.SourceFile) return null;

        var root = EffectiveWorkspaceRoot;
        var folder = string.IsNullOrEmpty(folderPath) ? root : Path.Combine(root, folderPath);
        Directory.CreateDirectory(folder);

        var explicitExt = Path.GetExtension(fileName?.Trim() ?? string.Empty);
        var extension = !string.IsNullOrEmpty(explicitExt) && (language.FileExtensions.Contains(explicitExt, StringComparer.OrdinalIgnoreCase) || language.Id == LanguageIds.Text)
            ? explicitExt
            : language.DefaultExtension();
        var baseName = SanitizeName(
            string.IsNullOrWhiteSpace(fileName) ? $"script_{DateTime.Now:HHmmss}" : Path.GetFileNameWithoutExtension(fileName.Trim()),
            "script");
        // "_2" rather than " (2)": a Python module whose name has spaces or brackets can't be imported.
        var name = baseName;
        for (var n = 2; File.Exists(Path.Combine(folder, name + extension)); n++) name = $"{baseName}_{n}";

        var path = Path.Combine(folder, name + extension);
        await File.WriteAllTextAsync(path, initialContent ?? language.NewFileTemplate, new UTF8Encoding(false));
        MarkChanged();
        return await LoadSourceFileAsync(RegisterSourceFile(path));
    }

    /// <summary>
    /// Renames a source file on disk and returns its new id (ids follow the path). The file keeps its language: renaming
    /// <c>main.py</c> to <c>main</c> gives <c>main.py</c>.
    /// </summary>
    public Task<SourceFileRename> RenameSourceFileAsync(string id, string newFileName)
    {
        if (!_knownFileLocations.TryGetValue(id, out var path) || !File.Exists(path))
        {
            throw new FileNotFoundException("That file no longer exists.", path);
        }

        var name = SanitizeName(Path.GetFileName(newFileName.Trim()), Path.GetFileName(path));
        if (string.IsNullOrEmpty(Path.GetExtension(name)) && Path.HasExtension(path))
        {
            name += Path.GetExtension(path);
        }

        var target = Path.Combine(Path.GetDirectoryName(path) ?? EffectiveWorkspaceRoot, name);
        if (string.Equals(path, target, StringComparison.Ordinal)) return Task.FromResult(new SourceFileRename(id, path));

        if (string.Equals(path, target, StringComparison.OrdinalIgnoreCase))
        {
            // Case-only rename: case-insensitive file systems need a two-step move, or File.Move does nothing.
            var temporary = target + $".rename_{Guid.NewGuid():N}";
            File.Move(path, temporary);
            File.Move(temporary, target);
        }
        else
        {
            if (File.Exists(target)) throw new IOException($"A file named '{name}' already exists here.");
            File.Move(path, target);
        }

        _knownFileLocations.TryRemove(id, out _);
        SourceFileState? state;
        lock (_sourceGate) _sourceFiles.Remove(id, out state);

        var newId = RegisterSourceFile(target);
        if (state != null)
        {
            state.Path = target;
            state.LastWriteTimeUtc = LastWriteTimeUtc(target);
            lock (_sourceGate) _sourceFiles[newId] = state;
        }

        MarkChanged();
        return Task.FromResult(new SourceFileRename(newId, _knownFileLocations[newId]));
    }

    private void DeleteSourceFile(string id)
    {
        if (_knownFileLocations.TryGetValue(id, out var path) && File.Exists(path)) File.Delete(path);
        _knownFileLocations.TryRemove(id, out _);
        lock (_sourceGate) _sourceFiles.Remove(id);
    }

    private static DateTime LastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.UtcNow;
        }
    }
}
