using System.Diagnostics;
using System.Text.Json;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Models.Server;
using PdfEditorApp.Plugins.CSharpEditor.Services.Activities;
using PdfEditorApp.Plugins.CSharpEditor.Services.Languages;
using PdfEditorApp.Plugins.CSharpEditor.Services.Templates;
using PdfEditorApp.Plugins.CSharpEditor.Services.Workspace;

namespace PdfEditorApp.Plugins.CSharpEditor.Services.Storage;

public partial class LocalScriptStorageService : IScriptStorageService, IDisposable
{
    private readonly string _baseDir;
    private readonly string _libraryRoot;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;
    private bool _disposed;

    private readonly string _workspaceStatePath;
    private string? _activeWorkspaceRootPath;

    // In-memory only: lets a document opened from outside the active workspace root (a loose file,
    // or a tab left open across a root switch) still be found and saved in place via Ctrl+S, without
    // resurrecting a persistent cross-root registry.
    // Concurrent: the workspace walks run on the thread pool (so a caller blocking on them can't deadlock the UI thread).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _knownFileLocations = new(StringComparer.OrdinalIgnoreCase);

    private class WorkspaceState
    {
        public string? ActiveRootPath { get; set; }
    }

    public string LibraryRootPath => _libraryRoot;

    // How many times the workspace has been walked to list its files or folders. Diagnostic: it lets a test or the
    // perf tool prove that switching a tab or returning to the Hub does not re-scan the whole workspace.
    private int _workspaceScanCount;
    public int WorkspaceScanCount => Volatile.Read(ref _workspaceScanCount);

    // Change stamps: consumers remember the value they last loaded at and reload only when it has moved, instead of
    // re-walking the whole workspace every time the user switches a tab or returns to the Hub.
    private long _structureVersion;
    private long _contentVersion;
    public long StructureVersion => Interlocked.Read(ref _structureVersion);
    public long ContentVersion => Interlocked.Read(ref _contentVersion);

    /// <param name="structural">An item appeared, disappeared, moved or was renamed (the Explorer tree changes); false for a save (titles and times change, the tree does not).</param>
    private void MarkChanged(bool structural = true)
    {
        Volatile.Write(ref _lastOwnChangeTick, Environment.TickCount64);
        BumpVersions(structural);
    }

    private void BumpVersions(bool structural)
    {
        if (structural) Interlocked.Increment(ref _structureVersion);
        Interlocked.Increment(ref _contentVersion);
    }

    // ── Changes made outside the studio ──────────────────────────────────────────────────────────────────────────
    // The studio's own operations bump the versions themselves; files changed behind its back (git, another editor)
    // are noticed by watching the active workspace folder, once StartWatchingForChanges has been called.

    /// <summary>Raised (on a background thread) once a burst of changes made outside the studio has settled.</summary>
    public event Action? ExternalChangeDetected;

    private const long OwnChangeEchoMilliseconds = 30000;
    private readonly object _watcherGate = new();
    private WorkspaceWatcher? _watcher;
    private bool _watchingEnabled;
    private long _lastOwnChangeTick;

    /// <summary>Starts watching the active workspace folder (and follows it when another folder is opened).</summary>
    public void StartWatchingForChanges()
    {
        _watchingEnabled = true;
        if (_initialized) RestartWatcher();
    }

    private void RestartWatcher()
    {
        if (!_watchingEnabled) return;

        lock (_watcherGate)
        {
            _watcher?.Dispose();
            _watcher = WorkspaceWatcher.TryStart(EffectiveWorkspaceRoot, OnChangedOnDisk);
        }
    }

    private void OnChangedOnDisk(bool structural)
    {
        // The file system reports the studio's own writes too, and the studio has already accounted for those.
        if (Environment.TickCount64 - Volatile.Read(ref _lastOwnChangeTick) < OwnChangeEchoMilliseconds) return;

        BumpVersions(structural);
        ExternalChangeDetected?.Invoke();
    }

    private string EffectiveWorkspaceRoot => _activeWorkspaceRootPath ?? _libraryRoot;
    public string ActiveWorkspaceRootPath => EffectiveWorkspaceRoot;
    public bool IsExternalWorkspaceActive => _activeWorkspaceRootPath != null;
    public event Action? ActiveWorkspaceChanged;

    private readonly RecentWorkspaceService _recentWorkspaces;
    public IRecentWorkspaceService RecentWorkspaces => _recentWorkspaces;

    // Walking a folder stops after this many files (or folders); see IsWorkspaceTruncated.
    private readonly int _workspaceFileLimit;
    private volatile bool _filesTruncated;
    private volatile bool _foldersTruncated;

    public int WorkspaceFileLimit => _workspaceFileLimit;

    private readonly WorkspaceFileIndex _fileIndex;
    private string? _indexedRoot;
    private long _indexedStructureVersion = -1;

    /// <summary>
    /// The names of every file in the active workspace, searchable at once ("Go to File"), however big the project is.
    /// Asking for it starts the background walk that fills it, and again after the workspace changed (another folder opened, a
    /// file created, deleted or renamed), so it is usually ready by the time it is searched.
    /// </summary>
    public WorkspaceFileIndex FileIndex
    {
        get
        {
            var root = EffectiveWorkspaceRoot;
            var version = StructureVersion;
            bool stale;
            lock (_fileIndex)
            {
                stale = !string.Equals(_indexedRoot, root, StringComparison.Ordinal) || _indexedStructureVersion != version;
                if (stale)
                {
                    _indexedRoot = root;
                    _indexedStructureVersion = version;
                }
            }

            if (stale) _ = _fileIndex.RebuildAsync(root);
            return _fileIndex;
        }
    }

    /// <summary>True when the last listing of the workspace stopped at <see cref="WorkspaceFileLimit"/>: the folder holds more than is listed.</summary>
    public bool IsWorkspaceTruncated => _filesTruncated || _foldersTruncated;

    /// <param name="languages">The languages whose plain source files (main.py) the workspace lists; the built-in ones by default.</param>
    /// <param name="workspaceFileLimit">How many files (or folders) a listing of the workspace includes before it stops; a folder with more is reported as truncated.</param>
    public LocalScriptStorageService(string? customBaseDir = null, LanguageRegistry? languages = null, int workspaceFileLimit = WorkspaceWalker.MaxFiles)
    {
        _workspaceFileLimit = workspaceFileLimit;
        _languages = languages ?? StudioLanguageServices.Default.Registry;
        _fileIndex = new WorkspaceFileIndex(IsWorkspaceFile);
        _baseDir = !string.IsNullOrEmpty(customBaseDir)
            ? customBaseDir
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FryPDF",
                "Plugins",
                "com.frypdf.plugin.csharpeditor");

        _libraryRoot = Path.Combine(_baseDir, "library");
        _workspaceStatePath = Path.Combine(_baseDir, "workspace_state.json");
        _recentWorkspaces = new RecentWorkspaceService(Path.Combine(_baseDir, "recent_workspaces.json"));

        Directory.CreateDirectory(_libraryRoot);
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;

        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;

            await LoadWorkspaceStateAsync();

            // Starter templates are offered one at a time from the "New from Template" gallery
            // (CodeTemplateLibrary via CSharpManagerViewModel.StarterTemplates); the library is never
            // auto-populated with all of them, so a first-run Explorer starts empty until the user
            // creates something.
            _initialized = true;
            RestartWatcher(); // The active folder is known now (a folder opened last time comes back).
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task LoadWorkspaceStateAsync()
    {
        if (!File.Exists(_workspaceStatePath)) return;

        try
        {
            var json = await File.ReadAllTextAsync(_workspaceStatePath);
            var state = JsonSerializer.Deserialize<WorkspaceState>(json);
            if (state?.ActiveRootPath != null && Directory.Exists(state.ActiveRootPath))
            {
                _activeWorkspaceRootPath = state.ActiveRootPath;
                _ = _recentWorkspaces.RecordWorkspaceOpenedAsync(state.ActiveRootPath, RecentWorkspaceKind.ProjectWorkspace);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CSharpEditorPlugin] Failed to load workspace state: {ex.Message}");
        }
    }

    private async Task SaveWorkspaceStateAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(new WorkspaceState { ActiveRootPath = _activeWorkspaceRootPath }, _jsonOptions);
            await File.WriteAllTextAsync(_workspaceStatePath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CSharpEditorPlugin] Failed to save workspace state: {ex.Message}");
        }
    }

    private string GetFolderPath(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath) ?? EffectiveWorkspaceRoot;
        var rel = Path.GetRelativePath(EffectiveWorkspaceRoot, dir);
        return rel == "." ? string.Empty : rel.Replace(Path.DirectorySeparatorChar, '/');
    }

    private async Task<string?> FindExistingFilePathAsync(string id, string extension)
    {
        if (File.Exists(id) && id.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            _knownFileLocations[id] = id;
            return id;
        }

        if (File.Exists(id + extension))
        {
            _knownFileLocations[id] = id + extension;
            return id + extension;
        }

        var root = EffectiveWorkspaceRoot;
        if (!string.IsNullOrEmpty(root))
        {
            var combined = Path.Combine(root, id);
            if (File.Exists(combined) && combined.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                _knownFileLocations[id] = combined;
                return combined;
            }

            var combinedExt = Path.Combine(root, id + extension);
            if (File.Exists(combinedExt))
            {
                _knownFileLocations[id] = combinedExt;
                return combinedExt;
            }
        }

        if (_knownFileLocations.TryGetValue(id, out var cached) &&
            cached.EndsWith(extension, StringComparison.OrdinalIgnoreCase) && File.Exists(cached))
        {
            return cached;
        }

        var candidates = await Task.Run(() => WorkspaceWalker.Files(root, f => f.EndsWith(extension, StringComparison.OrdinalIgnoreCase))).ConfigureAwait(false);
        var direct = candidates.FirstOrDefault(f => string.Equals(Path.GetFileName(f), $"{id}{extension}", StringComparison.OrdinalIgnoreCase));
        if (direct != null)
        {
            _knownFileLocations[id] = direct;
            return direct;
        }

        var directWithoutExt = candidates.FirstOrDefault(f => string.Equals(Path.GetFileName(f), id, StringComparison.OrdinalIgnoreCase));
        if (directWithoutExt != null)
        {
            _knownFileLocations[id] = directWithoutExt;
            return directWithoutExt;
        }

        foreach (var file in candidates)
        {
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(file));
                if (doc.RootElement.TryGetProperty("Id", out var idProp) &&
                    string.Equals(idProp.GetString(), id, StringComparison.OrdinalIgnoreCase))
                {
                    _knownFileLocations[id] = file;
                    return file;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    public async Task<List<WorkspaceItemSummary>> LoadWorkspaceSummariesAsync()
    {
        Interlocked.Increment(ref _workspaceScanCount);
        await EnsureInitializedAsync();
        var root = EffectiveWorkspaceRoot;
        var (files, filesTruncated) = await Task.Run(() =>
        {
            var found = WorkspaceWalker.Files(root, IsWorkspaceFile, out var truncated, _workspaceFileLimit);
            return (found, truncated);
        }).ConfigureAwait(false);
        _filesTruncated = filesTruncated;

        return await SummarizeFilesAsync(files, root);
    }

    // What the workspace lists about each of these files: a document's own title and id (read from the file), a source file's name.
    private async Task<List<WorkspaceItemSummary>> SummarizeFilesAsync(IReadOnlyList<string> files, string root)
    {
        var list = new List<WorkspaceItemSummary>();
        foreach (var file in files.Where(f => f.EndsWith(".frycs", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var script = JsonSerializer.Deserialize<ScriptDocumentItem>(json);
                if (script != null)
                {
                    _knownFileLocations[script.Id] = file;
                    list.Add(new WorkspaceItemSummary
                    {
                        Id = script.Id,
                        Title = script.Title,
                        Description = script.Description,
                        Category = script.Category,
                        Kind = WorkspaceItemKind.Script,
                        LastModified = script.LastModified,
                        ExecutionCount = script.ExecutionCount,
                        ExecutionMode = script.ExecutionMode,
                        FolderPath = GetFolderPath(file),
                        FileName = Path.GetFileName(file),
                        IsExternalRoot = IsExternalWorkspaceActive,
                        WorkspaceRootName = IsExternalWorkspaceActive ? Path.GetFileName(root.TrimEnd('/', '\\')) : null
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CSharpEditorPlugin] Skipping corrupted script file '{file}': {ex.Message}");
            }
        }

        foreach (var file in files.Where(f => f.EndsWith(".frynb", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var nb = JsonSerializer.Deserialize<NotebookDocumentItem>(json);
                if (nb != null)
                {
                    _knownFileLocations[nb.Id] = file;
                    list.Add(new WorkspaceItemSummary
                    {
                        Id = nb.Id,
                        Title = nb.Title,
                        Description = nb.Description,
                        Category = nb.Category,
                        Kind = WorkspaceItemKind.Notebook,
                        LastModified = nb.LastModified,
                        ExecutionCount = nb.ExecutionCount,
                        CellCount = nb.Cells.Count,
                        FolderPath = GetFolderPath(file),
                        FileName = Path.GetFileName(file),
                        IsExternalRoot = IsExternalWorkspaceActive,
                        WorkspaceRootName = IsExternalWorkspaceActive ? Path.GetFileName(root.TrimEnd('/', '\\')) : null
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CSharpEditorPlugin] Skipping corrupted notebook file '{file}': {ex.Message}");
            }
        }

        foreach (var file in files.Where(f => f.EndsWith(".fryserver", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var srv = JsonSerializer.Deserialize<FryServerDocumentItem>(json);
                if (srv != null)
                {
                    _knownFileLocations[srv.Id] = file;
                    list.Add(new WorkspaceItemSummary
                    {
                        Id = srv.Id,
                        Title = srv.Title,
                        Description = srv.Description,
                        Category = "API Server",
                        Kind = WorkspaceItemKind.Server,
                        LastModified = srv.LastModified,
                        CellCount = srv.Cells.Count,
                        FolderPath = GetFolderPath(file),
                        FileName = Path.GetFileName(file),
                        IsExternalRoot = IsExternalWorkspaceActive,
                        WorkspaceRootName = IsExternalWorkspaceActive ? Path.GetFileName(root.TrimEnd('/', '\\')) : null,
                        FileExtension = ".fryserver"
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CSharpEditorPlugin] Skipping corrupted server file '{file}': {ex.Message}");
            }
        }

        foreach (var file in files)
        {
            if (file.EndsWith(".frycs", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".frynb", StringComparison.OrdinalIgnoreCase) ||
                file.EndsWith(".fryserver", StringComparison.OrdinalIgnoreCase))
                continue;

            if (_languages.FindSourceFileLanguage(file) is { } language && language.Id != LanguageIds.Text)
            {
                list.Add(SourceFileSummary(file, language, root));
            }
            else
            {
                list.Add(GenericFileSummary(file, root));
            }
        }

        return list.OrderByDescending(x => x.LastModified).ToList();
    }

    private static readonly HashSet<string> KnownImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".svg", ".tiff", ".tif"
    };

    private WorkspaceItemSummary GenericFileSummary(string file, string root)
    {
        var ext = Path.GetExtension(file);
        var isImg = KnownImageExtensions.Contains(ext);
        return new WorkspaceItemSummary
        {
            Id = RegisterSourceFile(file),
            Title = Path.GetFileNameWithoutExtension(file),
            Description = isImg ? "Image asset" : (string.IsNullOrEmpty(ext) ? "File" : $"{ext.TrimStart('.').ToUpperInvariant()} file"),
            Category = isImg ? "Images" : "Files",
            Kind = WorkspaceItemKind.Script,
            LanguageId = LanguageIds.Text,
            LanguageName = isImg ? "Image" : (string.IsNullOrEmpty(ext) ? "File" : ext.TrimStart('.').ToUpperInvariant()),
            FileExtension = ext,
            IsSourceFile = false,
            LastModified = LastWriteTimeUtc(file),
            FolderPath = GetFolderPath(file),
            FileName = Path.GetFileName(file),
            IsExternalRoot = IsExternalWorkspaceActive,
            WorkspaceRootName = IsExternalWorkspaceActive ? Path.GetFileName(root.TrimEnd('/', '\\')) : null
        };
    }

    /// <summary>
    /// What the Explorer lists. A workspace that fits the limit is listed whole, every folder and document. A bigger one lists only its
    /// top folder here, and each folder's contents come from <see cref="ListFolderAsync"/> as the folder is opened: nothing is cut
    /// off, and nothing costs more than the folder being looked at.
    /// </summary>
    public async Task<WorkspaceListing> LoadExplorerListingAsync()
    {
        await EnsureInitializedAsync();
        var root = EffectiveWorkspaceRoot;
        return await _explorerListings.RunAsync((root, StructureVersion, ContentVersion), () => ListExplorerAsync(root)).ConfigureAwait(false);
    }

    // Listings of the same workspace asked for at the same time, at the same versions, share one walk: both studios'
    // Explorers list it at once in the background, and a visit can come while that runs. A finished listing is not kept,
    // so the next one (a Refresh, say) reads the disk again.
    private readonly SingleFlight<(string Root, long Structure, long Content), WorkspaceListing> _explorerListings = new();

    private async Task<WorkspaceListing> ListExplorerAsync(string root)
    {
        Interlocked.Increment(ref _workspaceScanCount);

        // Names only: cheap enough to ask before deciding how to list (the walk stops as soon as the limit is passed).
        var (files, truncated) = await Task.Run(() =>
        {
            // Every file VS Code's Explorer would show (all but .DS_Store and Thumbs.db).
            var found = WorkspaceWalker.Files(root, WorkspaceWalker.IsExplorerFile, out var cutOff, _workspaceFileLimit);
            return (found, cutOff);
        }).ConfigureAwait(false);
        if (truncated) return await ListFolderCoreAsync(root, string.Empty).ConfigureAwait(false);

        string Relative(string dir) => Path.GetRelativePath(root, dir).Replace(Path.DirectorySeparatorChar, '/');
        var (folders, closed) = await Task.Run(() =>
        {
            var walked = WorkspaceWalker.Folders(root, out _, out var notWalked, _workspaceFileLimit);
            return (walked.Select(Relative).ToList(), notWalked.Select(Relative).ToList());
        }).ConfigureAwait(false);
        _filesTruncated = false;
        _foldersTruncated = false;
        return new WorkspaceListing(folders, await SummarizeFilesAsync(files, root).ConfigureAwait(false), IsPartial: false, ClosedFolderPaths: closed);
    }

    /// <summary>One folder of the workspace, as a path from the root (empty for the root itself): its subfolders and the documents directly in it.</summary>
    public async Task<WorkspaceListing> ListFolderAsync(string relativeFolder)
    {
        await EnsureInitializedAsync();
        return await ListFolderCoreAsync(EffectiveWorkspaceRoot, relativeFolder ?? string.Empty).ConfigureAwait(false);
    }

    private async Task<WorkspaceListing> ListFolderCoreAsync(string root, string relativeFolder)
    {
        var folder = relativeFolder.Length == 0 ? root : Path.Combine(root, relativeFolder.Replace('/', Path.DirectorySeparatorChar));
        string[] directories;
        string[] files;
        try
        {
            (directories, files) = await Task.Run(() => (Directory.GetDirectories(folder), Directory.GetFiles(folder))).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new WorkspaceListing([], [], IsPartial: true);
        }

        // Everything in the folder but what VS Code's Explorer hides (.git, .DS_Store, ...): node_modules or bin are
        // listed too, each listed in turn when it is opened.
        var subfolders = directories
            .Where(d => !WorkspaceWalker.IsHiddenInExplorer(d))
            .Select(d => Path.GetRelativePath(root, d).Replace(Path.DirectorySeparatorChar, '/'))
            .ToList();
        var wanted = files.Where(WorkspaceWalker.IsExplorerFile).ToList();
        var truncated = wanted.Count > _workspaceFileLimit;
        if (truncated) wanted.RemoveRange(_workspaceFileLimit, wanted.Count - _workspaceFileLimit);
        return new WorkspaceListing(subfolders, await SummarizeFilesAsync(wanted, root).ConfigureAwait(false), IsPartial: true, IsTruncated: truncated);
    }

    /// <summary>
    /// Where a document lives, as a path from the workspace root (<c>/</c> separators), when it is known to this session and inside
    /// the active workspace; null otherwise. The Explorer of a big workspace uses it to open the folders down to the open document.
    /// </summary>
    public string? GetWorkspaceRelativePath(string documentId)
    {
        if (!_knownFileLocations.TryGetValue(documentId, out var path)) return null;

        var relative = Path.GetRelativePath(EffectiveWorkspaceRoot, path);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? null : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    public async Task<ScriptDocumentItem?> LoadScriptAsync(string id)
    {
        await EnsureInitializedAsync();
        if (IsSourceFileId(id)) return await LoadSourceFileAsync(id);
        var file = await FindExistingFilePathAsync(id, ".frycs");
        if (file == null) return null;

        try
        {
            var json = await File.ReadAllTextAsync(file).ConfigureAwait(false);
            return await Task.Run(() => JsonSerializer.Deserialize<ScriptDocumentItem>(json)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CSharpEditorPlugin] Failed to load script '{id}': {ex.Message}");
            return null;
        }
    }

    public async Task<bool> SaveScriptAsync(ScriptDocumentItem script, string? folderPath = null)
    {
        if (script.IsEphemeral) return false;

        // A source file is saved as its text, and only when that's safe without asking (see SaveSourceFileAsync).
        if (script.SourceFilePath != null || IsSourceFileId(script.Id))
        {
            return await SaveSourceFileAsync(script, overwriteChangesOnDisk: false);
        }

        var lang = _languages.Get(script.LanguageId);
        if (lang != null && lang.Storage == LanguageStorageKind.SourceFile)
        {
            var sourceFile = await CreateNewSourceFileAsync(script.LanguageId, script.Title, folderPath, script.Code);
            if (sourceFile != null)
            {
                script.Id = sourceFile.Id;
                script.SourceFilePath = sourceFile.SourceFilePath;
                script.Title = sourceFile.Title;
                script.LastModified = sourceFile.LastModified;
                return true;
            }
            return false;
        }

        try
        {
            script.LastModified = DateTime.UtcNow;
            var json = JsonSerializer.Serialize(script, _jsonOptions);

            var existing = await FindExistingFilePathAsync(script.Id, ".frycs");
            if (existing != null)
            {
                await File.WriteAllTextAsync(existing, json);
                MarkChanged(structural: false);
                return true;
            }

            await WriteNewDocumentAsync(script.Id, ".frycs", folderPath, json, script.Title);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CSharpEditorPlugin] Failed to save script '{script.Id}': {ex.Message}");
            return false;
        }
    }

    public async Task<NotebookDocumentItem?> LoadNotebookAsync(string id)
    {
        await EnsureInitializedAsync();
        var file = await FindExistingFilePathAsync(id, ".frynb");
        if (file == null && (id.EndsWith(".ipynb", StringComparison.OrdinalIgnoreCase) || id.EndsWith(".csnb", StringComparison.OrdinalIgnoreCase)))
        {
            file = await FindExistingFilePathAsync(id, Path.GetExtension(id));
        }
        if (file == null) return null;

        try
        {
            var json = await File.ReadAllTextAsync(file).ConfigureAwait(false);
            // A .ipynb is read as Jupyter wrote it (its cells, outputs and kernel); a .frynb or .csnb as FrySharp did.
            var nb = await Task.Run(() => NotebookFileFormat.Read(file, json, _languages)).ConfigureAwait(false);
            if (nb != null)
            {
                if (file.EndsWith(".ipynb", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".csnb", StringComparison.OrdinalIgnoreCase))
                {
                    nb.Id = Path.GetFileName(file);
                }
                bool migrated = false;
                foreach (var cell in nb.Cells)
                {
                    if (cell.Source?.Contains("4.154.0-preview.1.26454.9") == true)
                    {
                        cell.Source = cell.Source.Replace("4.154.0-preview.1.26454.9", "3.119.4");
                        migrated = true;
                    }
                }
                if (migrated)
                {
                    await SaveNotebookAsync(nb);
                }
            }
            return nb;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CSharpEditorPlugin] Failed to load notebook '{id}': {ex.Message}");
            return null;
        }
    }

    public async Task<bool> SaveNotebookAsync(NotebookDocumentItem notebook, string? folderPath = null)
    {
        if (notebook.IsEphemeral) return false;

        try
        {
            notebook.LastModified = DateTime.UtcNow;
            var ext = notebook.Id.EndsWith(".ipynb", StringComparison.OrdinalIgnoreCase) ? ".ipynb" :
                      notebook.Id.EndsWith(".csnb", StringComparison.OrdinalIgnoreCase) ? ".csnb" : ".frynb";
            var kind = ext == ".ipynb" ? NotebookFileKind.Ipynb : NotebookFileKind.Frynb;
            var json = NotebookFileFormat.Write(notebook, kind, _languages);

            var existing = await FindExistingFilePathAsync(notebook.Id, ext);
            if (existing != null)
            {
                await File.WriteAllTextAsync(existing, json);
                MarkChanged(structural: false);
                return true;
            }

            await WriteNewDocumentAsync(notebook.Id, ext, folderPath, json, notebook.Title);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CSharpEditorPlugin] Failed to save notebook '{notebook.Id}': {ex.Message}");
            return false;
        }
    }

    public async Task<FryServerDocumentItem?> LoadServerDocumentAsync(string id)
    {
        await EnsureInitializedAsync();
        var file = await FindExistingFilePathAsync(id, ".fryserver");
        if (file == null) return null;

        try
        {
            var json = await File.ReadAllTextAsync(file).ConfigureAwait(false);
            return await Task.Run(() => JsonSerializer.Deserialize<FryServerDocumentItem>(json, _jsonOptions)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CSharpEditorPlugin] Failed to load server doc '{id}': {ex.Message}");
            return null;
        }
    }

    public async Task<bool> SaveServerDocumentAsync(FryServerDocumentItem serverDoc, string? folderPath = null)
    {
        try
        {
            serverDoc.LastModified = DateTime.UtcNow;
            var json = JsonSerializer.Serialize(serverDoc, _jsonOptions);

            var existing = await FindExistingFilePathAsync(serverDoc.Id, ".fryserver");
            if (existing != null)
            {
                await File.WriteAllTextAsync(existing, json);
                MarkChanged(structural: false);
                return true;
            }

            await WriteNewDocumentAsync(serverDoc.Id, ".fryserver", folderPath, json, serverDoc.Title);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CSharpEditorPlugin] Failed to save server doc '{serverDoc.Id}': {ex.Message}");
            return false;
        }
    }

    public async Task<FryServerDocumentItem> CreateNewServerDocumentAsync(string title = "New Server", string? folderPath = null)
    {
        var doc = new FryServerDocumentItem
        {
            Title = title,
            ServerConfig = new FryServerConfiguration
            {
                Host = "localhost",
                Port = 5000,
                ApiPrefix = "/api",
                EnableCors = true
            },
            Cells = new()
            {
                new FryServerCellItem
                {
                    Type = FryServerCellType.Endpoint,
                    Title = "Get Status",
                    Method = "GET",
                    Route = "/status",
                    Source = "return Ok(new { status = \"healthy\", timestamp = DateTime.UtcNow });"
                }
            }
        };

        await SaveServerDocumentAsync(doc, folderPath);
        return doc;
    }

    private async Task WriteNewDocumentAsync(string id, string extension, string? folderPath, string json, string title)
    {
        var root = EffectiveWorkspaceRoot;
        var targetDir = string.IsNullOrEmpty(folderPath) ? root : Path.Combine(root, folderPath);
        Directory.CreateDirectory(targetDir);

        var safeName = SanitizeName(title, "Untitled");
        var finalName = safeName;
        var suffix = 1;
        while (File.Exists(Path.Combine(targetDir, $"{finalName}{extension}")))
        {
            suffix++;
            finalName = $"{safeName} ({suffix})";
        }

        var file = Path.Combine(targetDir, $"{finalName}{extension}");
        await File.WriteAllTextAsync(file, json);
        _knownFileLocations[id] = file;
        MarkChanged();
    }

    public async Task<ScriptDocumentItem> CreateNewScriptAsync(string title = "New Script", string? templateId = null, string? folderPath = null)
    {
        await EnsureInitializedAsync();
        var templates = CodeTemplateLibrary.GetTemplates();
        var template = templates.FirstOrDefault(t => t.Id == templateId && t.Kind == WorkspaceItemKind.Script)
                       ?? templates.FirstOrDefault(t => t.Kind == WorkspaceItemKind.Script);

        // A template of another language (Rust) is a plain source file of that language, not a C# document.
        if (template?.LanguageId is { } languageId &&
            await CreateNewSourceFileAsync(languageId, template.Id, folderPath, template.InitialCode) is { } sourceFile)
        {
            return sourceFile;
        }

        var script = new ScriptDocumentItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = string.IsNullOrWhiteSpace(title) ? (template?.Title ?? "New Script") : title,
            Description = template?.Description ?? "Custom C# script",
            Category = template?.Category ?? "Custom",
            ExecutionMode = "Statements",
            Code = template?.InitialCode ?? "// Write C# Statements or top-level code here\nConsole.WriteLine(\"Hello from FryPDF!\");",
            Notes = template?.Notes ?? "# Documentation & Notes\nWrite notes, algorithm specs, or test plans here.",
            TestCases = template?.TestCases ?? new List<TestCaseItem>(),
            Created = DateTime.UtcNow,
            LastModified = DateTime.UtcNow
        };

        try
        {
            await WriteNewDocumentAsync(script.Id, ".frycs", folderPath, JsonSerializer.Serialize(script, _jsonOptions), script.Title);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CSharpEditorPlugin] Failed to create script '{script.Id}': {ex.Message}");
        }

        return script;
    }

    public async Task<NotebookDocumentItem> CreateNewNotebookAsync(string title = "New Notebook", string? templateId = null, string? folderPath = null)
    {
        await EnsureInitializedAsync();
        var templates = CodeTemplateLibrary.GetTemplates();
        var template = templates.FirstOrDefault(t => t.Id == templateId && t.Kind == WorkspaceItemKind.Notebook);

        var resolvedTitle = !string.IsNullOrWhiteSpace(title) && title != "New Notebook"
            ? title
            : (template?.Title ?? "New Interactive Notebook");

        var notebook = new NotebookDocumentItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = resolvedTitle,
            Description = template?.Description ?? "Interactive cell-based notebook",
            Category = template?.Category ?? "Interactive",
            Created = DateTime.UtcNow,
            LastModified = DateTime.UtcNow
        };

        if (template?.Cells != null && template.Cells.Count > 0)
        {
            foreach (var cell in template.Cells)
            {
                notebook.Cells.Add(new NotebookCellItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Type = cell.Type,
                    Source = cell.Source,
                    Language = cell.Language,
                    IsMarkdownPreviewMode = cell.IsMarkdownPreviewMode
                });
            }
        }
        else
        {
            notebook.Cells.Add(new NotebookCellItem
            {
                Type = CellType.Markdown,
                Source = $"# 📓 {notebook.Title}\nWrite documentation or notes in this cell.",
                IsMarkdownPreviewMode = true
            });

            notebook.Cells.Add(new NotebookCellItem
            {
                Type = CellType.Code,
                Source = !string.IsNullOrWhiteSpace(template?.InitialCode)
                    ? template.InitialCode
                    : "// C# Code Cell\nConsole.WriteLine(\"Hello from Notebook cell!\");"
            });
        }

        try
        {
            await WriteNewDocumentAsync(notebook.Id, ".frynb", folderPath, JsonSerializer.Serialize(notebook, _jsonOptions), notebook.Title);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CSharpEditorPlugin] Failed to create notebook '{notebook.Id}': {ex.Message}");
        }

        return notebook;
    }

    public async Task DeleteItemAsync(string id)
    {
        if (IsSourceFileId(id))
        {
            DeleteSourceFile(id);
            MarkChanged();
            return;
        }

        var scriptFile = await FindExistingFilePathAsync(id, ".frycs");
        if (scriptFile != null)
        {
            File.Delete(scriptFile);
        }

        var nbFile = await FindExistingFilePathAsync(id, ".frynb");
        if (nbFile != null)
        {
            File.Delete(nbFile);
        }

        // Server documents too (deleting one from an Explorer used to do nothing).
        var serverFile = await FindExistingFilePathAsync(id, ".fryserver");
        if (serverFile != null)
        {
            File.Delete(serverFile);
        }

        _knownFileLocations.TryRemove(id, out _);
        MarkChanged();
    }

    public async Task<string> RenameDocumentFileAsync(string id, string newFileName)
    {
        await EnsureInitializedAsync();
        string? existing = null;
        foreach (var extension in new[] { ".frycs", ".frynb", ".fryserver" })
        {
            existing = await FindExistingFilePathAsync(id, extension);
            if (existing != null) break;
        }

        if (existing == null) throw new FileNotFoundException($"No file for document '{id}'.");

        var ext = Path.GetExtension(existing);
        var stem = Path.GetFileName(newFileName.Trim());
        if (stem.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) stem = stem[..^ext.Length];
        var name = SanitizeName(stem, Path.GetFileNameWithoutExtension(existing)) + ext;
        var target = Path.Combine(Path.GetDirectoryName(existing)!, name);
        if (string.Equals(target, existing, StringComparison.Ordinal)) return name;

        // Only a change of case is the same file on a case-insensitive disk; anything else must not replace a file.
        if (File.Exists(target) && !string.Equals(target, existing, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"A file named '{name}' already exists here.");
        }

        File.Move(existing, target);
        _knownFileLocations[id] = target;

        // The title inside follows the name, so every list that shows titles agrees with the Explorer.
        var title = Path.GetFileNameWithoutExtension(name);
        switch (ext.ToLowerInvariant())
        {
            case ".frycs" when JsonSerializer.Deserialize<ScriptDocumentItem>(await File.ReadAllTextAsync(target)) is { } script:
                script.Title = title;
                await File.WriteAllTextAsync(target, JsonSerializer.Serialize(script, _jsonOptions));
                break;
            case ".frynb" when JsonSerializer.Deserialize<NotebookDocumentItem>(await File.ReadAllTextAsync(target)) is { } notebook:
                notebook.Title = title;
                await File.WriteAllTextAsync(target, JsonSerializer.Serialize(notebook, _jsonOptions));
                break;
            case ".fryserver" when JsonSerializer.Deserialize<FryServerDocumentItem>(await File.ReadAllTextAsync(target)) is { } server:
                server.Title = title;
                await File.WriteAllTextAsync(target, JsonSerializer.Serialize(server, _jsonOptions));
                break;
        }

        MarkChanged();
        return name;
    }

    public async Task<string> DuplicateFileAsync(string relativePath)
    {
        await EnsureInitializedAsync();
        var root = EffectiveWorkspaceRoot;
        var source = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(source)) throw new FileNotFoundException($"'{relativePath}' is not a file of the workspace.");

        var folder = Path.GetDirectoryName(source)!;
        var ext = Path.GetExtension(source);
        var stem = Path.GetFileNameWithoutExtension(source) + " copy";
        var target = Path.Combine(folder, stem + ext);
        for (var n = 2; File.Exists(target); n++) target = Path.Combine(folder, $"{stem} {n}{ext}");

        var isDocument = ext.Equals(".frycs", StringComparison.OrdinalIgnoreCase) || ext.Equals(".frynb", StringComparison.OrdinalIgnoreCase)
                         || ext.Equals(".fryserver", StringComparison.OrdinalIgnoreCase);
        if (isDocument && System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(source)) is System.Text.Json.Nodes.JsonObject document)
        {
            // Two files with one id would open each other: the copy is a document of its own.
            var id = Guid.NewGuid().ToString("N");
            document["Id"] = id;
            document["Title"] = Path.GetFileNameWithoutExtension(target);
            await File.WriteAllTextAsync(target, document.ToJsonString(_jsonOptions));
            _knownFileLocations[id] = target;
        }
        else
        {
            File.Copy(source, target);
        }

        MarkChanged();
        return Path.GetRelativePath(root, target).Replace(Path.DirectorySeparatorChar, '/');
    }

    public Task<List<string>> LoadFolderPathsAsync()
    {
        Interlocked.Increment(ref _workspaceScanCount);
        var root = EffectiveWorkspaceRoot;
        return Task.Run(() =>
        {
            var folders = WorkspaceWalker.Folders(root, out var truncated, _workspaceFileLimit);
            _foldersTruncated = truncated;
            return folders
                .Select(dir => Path.GetRelativePath(root, dir).Replace(Path.DirectorySeparatorChar, '/'))
                .ToList();
        });
    }

    public Task<string> CreateFolderAsync(string? parentFolderPath, string desiredName)
    {
        var root = EffectiveWorkspaceRoot;
        var parentDir = string.IsNullOrEmpty(parentFolderPath) ? root : Path.Combine(root, parentFolderPath);
        Directory.CreateDirectory(parentDir);

        var safeName = SanitizeName(desiredName, "New Folder");
        var finalName = safeName;
        var suffix = 1;
        while (Directory.Exists(Path.Combine(parentDir, finalName)))
        {
            suffix++;
            finalName = $"{safeName} ({suffix})";
        }

        Directory.CreateDirectory(Path.Combine(parentDir, finalName));
        MarkChanged();

        var relativePath = string.IsNullOrEmpty(parentFolderPath) ? finalName : $"{parentFolderPath}/{finalName}";
        return Task.FromResult(relativePath);
    }

    public Task<string> RenameFolderAsync(string folderPath, string newName)
    {
        var root = EffectiveWorkspaceRoot;
        var sourceDir = Path.Combine(root, folderPath);
        var parentRelative = Path.GetDirectoryName(folderPath)?.Replace(Path.DirectorySeparatorChar, '/') ?? string.Empty;
        var parentDir = string.IsNullOrEmpty(parentRelative) ? root : Path.Combine(root, parentRelative);
        var safeName = SanitizeName(newName, "New Folder");
        var destDir = Path.Combine(parentDir, safeName);
        var newRelativePath = string.IsNullOrEmpty(parentRelative) ? safeName : $"{parentRelative}/{safeName}";

        if (string.Equals(sourceDir, destDir, StringComparison.Ordinal))
        {
            return Task.FromResult(newRelativePath);
        }

        if (string.Equals(sourceDir, destDir, StringComparison.OrdinalIgnoreCase))
        {
            // Case-only rename: case-insensitive-but-preserving filesystems (default on macOS/Windows)
            // need a two-step move through a temp name, or Directory.Move is a silent no-op.
            var tempDir = Path.Combine(parentDir, $"{safeName}__rename_{Guid.NewGuid():N}");
            Directory.Move(sourceDir, tempDir);
            Directory.Move(tempDir, destDir);
            MarkChanged();
            return Task.FromResult(newRelativePath);
        }

        if (Directory.Exists(destDir))
        {
            throw new IOException($"A folder named '{safeName}' already exists here.");
        }

        Directory.Move(sourceDir, destDir);
        MarkChanged();
        return Task.FromResult(newRelativePath);
    }

    public Task DeleteFolderAsync(string folderPath)
    {
        var dir = Path.Combine(EffectiveWorkspaceRoot, folderPath);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
            MarkChanged();
        }
        return Task.CompletedTask;
    }

    private static string SanitizeName(string name, string fallback)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            trimmed = trimmed.Replace(c, '_');
        }
        return trimmed;
    }

    public async Task<List<ScriptProjectItem>> LoadScriptsAsync()
    {
        var summaries = await LoadWorkspaceSummariesAsync();
        var results = new List<ScriptProjectItem>();
        foreach (var s in summaries.Where(x => !x.IsSourceFile && !IsSourceFileId(x.Id)))
        {
            if (s.IsNotebook)
            {
                var nb = await LoadNotebookAsync(s.Id);
                if (nb != null)
                {
                    results.Add(new ScriptProjectItem
                    {
                        Id = nb.Id,
                        Title = nb.Title,
                        Description = nb.Description,
                        IsNotebook = true,
                        Category = nb.Category,
                        Tag = "NOTEBOOK",
                        Cells = nb.Cells,
                        CreatedAt = nb.Created,
                        LastModified = nb.LastModified
                    });
                }
            }
            else
            {
                var sc = await LoadScriptAsync(s.Id);
                if (sc != null)
                {
                    results.Add(new ScriptProjectItem
                    {
                        Id = sc.Id,
                        Title = sc.Title,
                        Description = sc.Description,
                        IsNotebook = false,
                        Category = sc.Category,
                        Tag = "SCRIPT",
                        Code = sc.Code,
                        ExecutionMode = sc.ExecutionMode,
                        CreatedAt = sc.Created,
                        LastModified = sc.LastModified
                    });
                }
            }
        }
        return results;
    }

    public async Task SaveScriptsAsync(IEnumerable<ScriptProjectItem> scripts)
    {
        foreach (var item in scripts)
        {
            if (item.IsNotebook)
            {
                await SaveNotebookAsync(new NotebookDocumentItem
                {
                    Id = item.Id,
                    Title = item.Title,
                    Description = item.Description,
                    Category = item.Category,
                    Cells = item.Cells,
                    LastModified = item.LastModified
                });
            }
            else
            {
                await SaveScriptAsync(new ScriptDocumentItem
                {
                    Id = item.Id,
                    Title = item.Title,
                    Description = item.Description,
                    Category = item.Category,
                    Code = item.Code,
                    ExecutionMode = item.ExecutionMode,
                    LastModified = item.LastModified
                });
            }
        }
    }

    private static string ResolveProjectRoot(string folder)
    {
        try
        {
            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var current = new DirectoryInfo(folder);
            var candidate = folder;

            while (current != null && current.Parent != null)
            {
                if (string.Equals(current.FullName, userHome, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (Directory.Exists(Path.Combine(current.FullName, ".frysharp")) ||
                    Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                    Directory.GetFiles(current.FullName, "*.csproj").Length > 0 ||
                    Directory.GetFiles(current.FullName, "*.frycsproj").Length > 0 ||
                    Directory.GetFiles(current.FullName, "*.sln").Length > 0)
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            return candidate;
        }
        catch
        {
            return folder;
        }
    }

    public async Task<OpenProjectResult> OpenExternalProjectAsync(string rawPath)
    {
        await EnsureInitializedAsync();

        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return new OpenProjectResult(false, "Project path cannot be empty.");
        }

        var path = rawPath.Trim();

        if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var extractDirName = Path.GetFileNameWithoutExtension(path);
                var parentDir = Path.GetDirectoryName(path) ?? _libraryRoot;
                var extractDir = Path.Combine(parentDir, extractDirName);
                if (Directory.Exists(extractDir))
                {
                    extractDir = Path.Combine(parentDir, $"{extractDirName}_{DateTime.UtcNow:yyyyMMddHHmmss}");
                }
                Directory.CreateDirectory(extractDir);
                System.IO.Compression.ZipFile.ExtractToDirectory(path, extractDir);
                path = extractDir;
            }
            catch (Exception ex)
            {
                return new OpenProjectResult(false, $"Failed to unpack project archive: {ex.Message}");
            }
        }

        if (Directory.Exists(path))
        {
            return await OpenFolderAsync(path);
        }

        if (!File.Exists(path))
        {
            return new OpenProjectResult(false, $"File or directory not found: '{path}'");
        }

        var parentFolder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parentFolder) && Directory.Exists(parentFolder))
        {
            var hasProjectMarker = Directory.Exists(Path.Combine(parentFolder, ".frysharp")) ||
                                   Directory.Exists(Path.Combine(parentFolder, ".git")) ||
                                   Directory.GetFiles(parentFolder, "*.csproj").Length > 0 ||
                                   Directory.GetFiles(parentFolder, "*.frycsproj").Length > 0;

            if (hasProjectMarker)
            {
                var currentRoot = _activeWorkspaceRootPath;
                var isInsideCurrentWorkspace = !string.IsNullOrWhiteSpace(currentRoot) &&
                    (string.Equals(parentFolder, currentRoot, StringComparison.OrdinalIgnoreCase) ||
                     parentFolder.StartsWith(currentRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

                if (!isInsideCurrentWorkspace)
                {
                    var resolvedRoot = ResolveProjectRoot(parentFolder);
                    _activeWorkspaceRootPath = string.Equals(resolvedRoot, _libraryRoot, StringComparison.OrdinalIgnoreCase) ? null : resolvedRoot;
                    await SaveWorkspaceStateAsync().ConfigureAwait(false);
                    await _recentWorkspaces.RecordWorkspaceOpenedAsync(resolvedRoot, RecentWorkspaceKind.ProjectWorkspace).ConfigureAwait(false);
                    MarkChanged();
                    RestartWatcher();
                    ActiveWorkspaceChanged?.Invoke();
                }
            }
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();

        // Legacy .frycsproj/.frynbproj files from older versions: just open their containing folder —
        // the real-filesystem walk in LoadWorkspaceSummariesAsync picks up everything in it directly.
        if (ext is ".frycsproj" or ".frynbproj")
        {
            var folder = Path.GetDirectoryName(path);
            return !string.IsNullOrEmpty(folder) && Directory.Exists(folder)
                ? await OpenFolderAsync(folder)
                : new OpenProjectResult(false, $"Could not resolve folder for '{path}'.");
        }

        if (ext == ".frynb")
        {
            return await OpenLooseNotebookAsync(path);
        }

        if (ext == ".fryserver")
        {
            return await OpenLooseServerAsync(path);
        }

        if (ext == ".frycs")
        {
            return await OpenLooseScriptAsync(path);
        }

        if (ext is ".cs" or ".csx")
        {
            return await OpenCsSourceFileAsync(path);
        }

        if (_languages.FindSourceFileLanguage(path) is { } sourceLanguage)
        {
            var id = RegisterSourceFile(path);
            await _recentWorkspaces.RecordWorkspaceOpenedAsync(path, RecentWorkspaceKind.StandaloneScript).ConfigureAwait(false);
            return new OpenProjectResult(
                Success: true,
                Message: $"Opened {sourceLanguage.DisplayName} file '{Path.GetFileName(path)}'",
                PrimaryDocumentId: id,
                PrimaryDocumentKind: WorkspaceItemKind.Script,
                DocumentsLoadedCount: 1);
        }

        if (ext == ".csproj")
        {
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                return await OpenFolderAsync(folder);
            }
        }

        if (File.Exists(path))
        {
            var id = RegisterSourceFile(path);
            await _recentWorkspaces.RecordWorkspaceOpenedAsync(path, RecentWorkspaceKind.StandaloneScript).ConfigureAwait(false);
            return new OpenProjectResult(
                Success: true,
                Message: $"Opened file '{Path.GetFileName(path)}'",
                PrimaryDocumentId: id,
                PrimaryDocumentKind: WorkspaceItemKind.Script,
                DocumentsLoadedCount: 1);
        }

        var sourceExtensions = string.Concat(_languages.SourceFileLanguages.SelectMany(l => l.FileExtensions).Select(e => ", " + e));
        return new OpenProjectResult(false, $"Unsupported project file format: '{ext}'. Supported formats: .frycsproj, .frynbproj, .frycs, .frynb, .fryserver, .cs, .csx, .csproj, .zip{sourceExtensions}");
    }

    private async Task<OpenProjectResult> OpenFolderAsync(string dirPath)
    {
        var full = Path.GetFullPath(dirPath.TrimEnd('/', '\\'));
        if (!Directory.Exists(full))
        {
            return new OpenProjectResult(false, $"Folder not found: '{full}'");
        }

        _activeWorkspaceRootPath = string.Equals(full, _libraryRoot, StringComparison.OrdinalIgnoreCase) ? null : full;
        await SaveWorkspaceStateAsync();
        await _recentWorkspaces.RecordWorkspaceOpenedAsync(full, RecentWorkspaceKind.ProjectWorkspace).ConfigureAwait(false);
        MarkChanged();
        RestartWatcher();
        ActiveWorkspaceChanged?.Invoke();

        var summaries = await LoadWorkspaceSummariesAsync();
        var primary = summaries.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        var folderName = Path.GetFileName(full) is { Length: > 0 } n ? n : full;

        return new OpenProjectResult(
            Success: true,
            Message: $"Opened folder '{folderName}' ({summaries.Count} document{(summaries.Count == 1 ? "" : "s")})",
            PrimaryDocumentId: primary?.Id,
            PrimaryDocumentKind: primary?.Kind,
            DocumentsLoadedCount: summaries.Count);
    }

    private async Task<OpenProjectResult> OpenLooseNotebookAsync(string filePath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            var nb = await Task.Run(() => JsonSerializer.Deserialize<NotebookDocumentItem>(json)).ConfigureAwait(false);
            if (nb == null)
            {
                return new OpenProjectResult(false, $"Failed to parse notebook JSON in '{Path.GetFileName(filePath)}'.");
            }

            _knownFileLocations[nb.Id] = filePath;
            await _recentWorkspaces.RecordWorkspaceOpenedAsync(filePath, RecentWorkspaceKind.StandaloneNotebook).ConfigureAwait(false);

            return new OpenProjectResult(
                Success: true,
                Message: $"Loaded notebook '{nb.Title}'",
                PrimaryDocumentId: nb.Id,
                PrimaryDocumentKind: WorkspaceItemKind.Notebook,
                DocumentsLoadedCount: 1);
        }
        catch (Exception ex)
        {
            return new OpenProjectResult(false, $"Failed to open notebook: {ex.Message}");
        }
    }

    private async Task<OpenProjectResult> OpenLooseServerAsync(string filePath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            var server = await Task.Run(() => JsonSerializer.Deserialize<FryServerDocumentItem>(json)).ConfigureAwait(false);
            if (server == null)
            {
                return new OpenProjectResult(false, $"Failed to parse API server JSON in '{Path.GetFileName(filePath)}'.");
            }

            _knownFileLocations[server.Id] = filePath;

            return new OpenProjectResult(
                Success: true,
                Message: $"Loaded API server '{server.Title}'",
                PrimaryDocumentId: server.Id,
                PrimaryDocumentKind: WorkspaceItemKind.Server,
                DocumentsLoadedCount: 1);
        }
        catch (Exception ex)
        {
            return new OpenProjectResult(false, $"Failed to open API server: {ex.Message}");
        }
    }

    private async Task<OpenProjectResult> OpenLooseScriptAsync(string filePath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var script = JsonSerializer.Deserialize<ScriptDocumentItem>(json);
            if (script == null)
            {
                return new OpenProjectResult(false, $"Failed to parse script JSON in '{Path.GetFileName(filePath)}'.");
            }

            _knownFileLocations[script.Id] = filePath;
            await _recentWorkspaces.RecordWorkspaceOpenedAsync(filePath, RecentWorkspaceKind.StandaloneScript).ConfigureAwait(false);

            return new OpenProjectResult(
                Success: true,
                Message: $"Loaded script '{script.Title}'",
                PrimaryDocumentId: script.Id,
                PrimaryDocumentKind: WorkspaceItemKind.Script,
                DocumentsLoadedCount: 1);
        }
        catch (Exception ex)
        {
            return new OpenProjectResult(false, $"Failed to open script: {ex.Message}");
        }
    }

    private async Task<OpenProjectResult> OpenCsSourceFileAsync(string filePath)
    {
        try
        {
            var code = await File.ReadAllTextAsync(filePath);
            var dir = Path.GetDirectoryName(filePath) ?? _libraryRoot;
            var baseName = Path.GetFileNameWithoutExtension(filePath);
            var frycsPath = Path.Combine(dir, $"{baseName}.frycs");

            ScriptDocumentItem script;
            if (File.Exists(frycsPath))
            {
                var existingJson = await File.ReadAllTextAsync(frycsPath);
                script = JsonSerializer.Deserialize<ScriptDocumentItem>(existingJson) ?? new ScriptDocumentItem();
                script.Code = code;
                script.LastModified = DateTime.UtcNow;
                await File.WriteAllTextAsync(frycsPath, JsonSerializer.Serialize(script, _jsonOptions));
            }
            else
            {
                script = new ScriptDocumentItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Title = baseName,
                    Category = "Imported",
                    Description = $"Imported from {Path.GetFileName(filePath)}",
                    Code = code,
                    ExecutionMode = code.Contains("static void Main") || code.Contains("class ") ? "Program" : "Statements",
                    Created = File.GetCreationTimeUtc(filePath),
                    LastModified = File.GetLastWriteTimeUtc(filePath)
                };
                await File.WriteAllTextAsync(frycsPath, JsonSerializer.Serialize(script, _jsonOptions));
            }

            _knownFileLocations[script.Id] = frycsPath;

            return new OpenProjectResult(
                Success: true,
                Message: $"Imported C# script '{script.Title}'",
                PrimaryDocumentId: script.Id,
                PrimaryDocumentKind: WorkspaceItemKind.Script,
                DocumentsLoadedCount: 1);
        }
        catch (Exception ex)
        {
            return new OpenProjectResult(false, $"Failed to import C# file: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_watcherGate)
        {
            _watcher?.Dispose();
            _watcher = null;
        }

        _initLock?.Dispose();
    }
}
