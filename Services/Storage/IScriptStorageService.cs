using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Models.Server;
using PdfEditorApp.Plugins.CSharpEditor.Services.Languages;
using PdfEditorApp.Plugins.CSharpEditor.Services.Workspace;

namespace PdfEditorApp.Plugins.CSharpEditor.Services.Storage;

public interface IScriptStorageService
{
    string LibraryRootPath { get; }

    /// <summary>The single active workspace root currently shown in the Explorer: an opened folder, or <see cref="LibraryRootPath"/> when none is open.</summary>
    string ActiveWorkspaceRootPath { get; }
    bool IsExternalWorkspaceActive { get; }
    event Action? ActiveWorkspaceChanged;

    /// <summary>Persistent store of recent workspaces, projects, and standalone notebooks.</summary>
    IRecentWorkspaceService RecentWorkspaces { get; }

    /// <summary>
    /// Moves whenever the set or arrangement of items changes (one created, deleted, renamed or moved, or another folder
    /// opened). A consumer that remembers the value it loaded at can skip reloading the workspace while it hasn't moved.
    /// </summary>
    long StructureVersion { get; }

    /// <summary>
    /// The names of every file in the active workspace, held in memory so "Go to File" can search a project of any size. Reading
    /// this starts (or restarts, after the workspace changed) the background walk that fills it.
    /// </summary>
    WorkspaceFileIndex FileIndex { get; }

    /// <summary>How many files (or folders) a listing of the workspace includes before it stops.</summary>
    int WorkspaceFileLimit { get; }

    /// <summary>True when the last listing of the workspace stopped at <see cref="WorkspaceFileLimit"/>: the folder holds more than is listed, and the UI should say so.</summary>
    bool IsWorkspaceTruncated { get; }

    /// <summary>Moves on every change <see cref="StructureVersion"/> reports, and on every save (titles and modified times change).</summary>
    long ContentVersion { get; }

    /// <summary>
    /// Raised (on a background thread) once files changed outside the studio (git, another editor) have settled; the
    /// versions above have already moved. Only raised where the storage was told to watch for changes.
    /// </summary>
    event Action? ExternalChangeDetected;

    /// <summary>Raised (on a background thread) with the files other programs changed; null when any file may have changed (the system lost track).</summary>
    event Action<IReadOnlyCollection<string>?>? ExternalFilesChanged;

    Task<List<WorkspaceItemSummary>> LoadWorkspaceSummariesAsync();

    /// <summary>
    /// What the Explorer draws: the whole workspace when it fits <see cref="WorkspaceFileLimit"/>, otherwise only the top folder's
    /// contents (see <see cref="WorkspaceListing.IsPartial"/>), the rest being listed folder by folder with <see cref="ListFolderAsync"/>.
    /// </summary>
    /// <param name="reuseCurrent">True to take the last listing when the workspace hasn't changed since (a first visit, a
    /// catch-up); false (a Refresh) always reads the disk.</param>
    Task<WorkspaceListing> LoadExplorerListingAsync(bool reuseCurrent = false);

    /// <summary>The subfolders and documents directly in one folder of the workspace (a path from the root; empty for the root).</summary>
    Task<WorkspaceListing> ListFolderAsync(string relativeFolder);

    /// <summary>Where a document known to this session lives, as a path from the workspace root; null when unknown or outside the workspace.</summary>
    string? GetWorkspaceRelativePath(string documentId);
    Task<ScriptDocumentItem?> LoadScriptAsync(string id);
    Task<bool> SaveScriptAsync(ScriptDocumentItem script, string? folderPath = null);
    Task<NotebookDocumentItem?> LoadNotebookAsync(string id);
    Task<bool> SaveNotebookAsync(NotebookDocumentItem notebook, string? folderPath = null);

    /// <summary>Moves a document's file to the machine's Trash (where it can be put back). False when it couldn't be moved there; nothing was deleted then.</summary>
    Task<bool> MoveItemToTrashAsync(string id);

    /// <summary>Moves a workspace folder (by its workspace-relative path) to the machine's Trash. False when it couldn't be moved there.</summary>
    Task<bool> MoveFolderToTrashAsync(string folderPath);

    /// <summary>Save As: writes the notebook to <paramref name="filePath"/> in the format its extension names, and makes that file the notebook's. Returns the file written (null when it couldn't be).</summary>
    Task<string?> SaveNotebookAsAsync(NotebookDocumentItem notebook, string filePath);

    /// <summary>Save As for a script or source file: writes <paramref name="text"/> to <paramref name="filePath"/> and returns that file as a document.</summary>
    Task<ScriptDocumentItem?> SaveSourceFileAsAsync(string filePath, string text);

    /// <summary>Save As for an API server document: writes it to <paramref name="filePath"/> and makes that file the document's. Returns the file written (null when it couldn't be).</summary>
    Task<string?> SaveServerDocumentAsAsync(FryServerDocumentItem serverDoc, string filePath);
    Task<FryServerDocumentItem?> LoadServerDocumentAsync(string id);
    Task<bool> SaveServerDocumentAsync(FryServerDocumentItem serverDoc, string? folderPath = null);
    Task<ScriptDocumentItem> CreateNewScriptAsync(string title = "New Script", string? templateId = null, string? folderPath = null);
    Task<NotebookDocumentItem> CreateNewNotebookAsync(string title = "New Notebook", string? templateId = null, string? folderPath = null);
    Task<FryServerDocumentItem> CreateNewServerDocumentAsync(string title = "New Server", string? folderPath = null);
    Task DeleteItemAsync(string id);

    Task<List<string>> LoadFolderPathsAsync();
    Task<string> CreateFolderAsync(string? parentFolderPath, string desiredName);
    Task<string> RenameFolderAsync(string folderPath, string newName);
    Task DeleteFolderAsync(string folderPath);

    Task<List<ScriptProjectItem>> LoadScriptsAsync();
    Task SaveScriptsAsync(IEnumerable<ScriptProjectItem> scripts);

    Task<OpenProjectResult> OpenExternalProjectAsync(string path);

    /// <summary>The languages whose plain source files (main.py) the workspace lists alongside its documents.</summary>
    LanguageRegistry Languages { get; }

    /// <summary>Creates a source file of a language (script_HHmmss.py, or <paramref name="fileName"/>) with its starter text (or <paramref name="initialContent"/>).</summary>
    Task<ScriptDocumentItem?> CreateNewSourceFileAsync(string languageId, string? fileName = null, string? folderPath = null, string? initialContent = null);

    /// <summary>Renames a source file on disk; its id changes with its path.</summary>
    Task<SourceFileRename> RenameSourceFileAsync(string id, string newFileName);

    /// <summary>
    /// Renames a document's file (.frycs, .frynb, .fryserver) in its folder, as renaming it in an Explorer does: the file
    /// takes the new name (its extension is kept) and its title follows. Returns the new file name. Throws
    /// <see cref="IOException"/> when another file already has that name, <see cref="FileNotFoundException"/> when the
    /// document has no file.
    /// </summary>
    Task<string> RenameDocumentFileAsync(string id, string newFileName);

    /// <summary>
    /// Copies a file of the workspace next to itself ("name copy.ext", "name copy 2.ext", ...), as VS Code's Duplicate
    /// does. A copied document gets an id of its own and the copy's name as its title. Returns the copy's path from the
    /// workspace root (<c>/</c> separators).
    /// </summary>
    Task<string> DuplicateFileAsync(string relativePath);

    /// <summary>
    /// Writes a source file's text. Without <paramref name="overwriteChangesOnDisk"/> it writes only when the text changed
    /// and nothing else changed the file since it was read (<see cref="SaveScriptAsync"/> saves source files this way).
    /// </summary>
    Task<bool> SaveSourceFileAsync(ScriptDocumentItem document, bool overwriteChangesOnDisk);
}

public record OpenProjectResult(
    bool Success,
    string Message,
    string? PrimaryDocumentId = null,
    WorkspaceItemKind? PrimaryDocumentKind = null,
    int DocumentsLoadedCount = 0);

/// <summary>A renamed source file's new id and path.</summary>
public sealed record SourceFileRename(string Id, string FilePath);
