using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.AI;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Models.AI;
using PdfEditorApp.Plugins.CSharpEditor.Services.Documentation;
using PdfEditorApp.Plugins.CSharpEditor.Services.Extensibility;
using PdfEditorApp.Plugins.CSharpEditor.Services.Roslyn;
using PdfEditorApp.Plugins.CSharpEditor.Services.Storage;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio;

using PdfEditorApp.Plugins.CSharpEditor.Services.Processes;
using PdfEditorApp.Plugins.CSharpEditor.Services.Workspace.Git;

namespace PdfEditorApp.Plugins.CSharpEditor.Services.AI.Tools;

/// <summary>
/// Registers autonomous agent tools with Microsoft.Extensions.AI via <see cref="AIFunctionFactory"/>.
/// Provides workspace file operations, active editor inspection and patching, Roslyn compilation diagnostics,
/// shell execution, git inspection, and live studio metaprogramming.
/// </summary>
public partial class AiAgentToolRegistry
{
    private readonly IScriptStorageService? _storageService;
    private readonly CSharpCodeStudioViewModel? _codeStudioViewModel;
    private readonly RoslynCompilerService? _compilerService;
    private readonly CustomizationManager? _customizationManager;
    private readonly DocumentationService? _documentationService;
    private readonly Action<ModifiedFileItem>? _onFileModified;
    private readonly IProcessLauncher _processLauncher;
    private readonly IGitService? _gitService;

    public Action<AgentStepItem>? OnStepUpdate { get; set; }

    public AiAgentToolRegistry(
        IScriptStorageService? storageService = null,
        CSharpCodeStudioViewModel? codeStudioViewModel = null,
        RoslynCompilerService? compilerService = null,
        CustomizationManager? customizationManager = null,
        DocumentationService? documentationService = null,
        Action<ModifiedFileItem>? onFileModified = null,
        IProcessLauncher? processLauncher = null,
        IGitService? gitService = null)
    {
        _storageService = storageService;
        _codeStudioViewModel = codeStudioViewModel;
        _compilerService = compilerService ?? new RoslynCompilerService();
        _customizationManager = customizationManager;
        _documentationService = documentationService ?? new DocumentationService();
        _onFileModified = onFileModified;
        _processLauncher = processLauncher ?? new ProcessLauncher();
        _gitService = gitService ?? codeStudioViewModel?.Git.GitService;
    }

    /// <summary>
    /// Builds the list of AITools ready to pass to ChatOptions.Tools or FunctionInvokingChatClient.
    /// If compact is true, returns only the core autonomous coding and active editor tools.
    /// </summary>
    public IList<AITool> BuildToolList(bool compact = false)
    {
        if (compact)
        {
            return new List<AITool>
            {
                AIFunctionFactory.Create(ListWorkspaceFiles, "list_workspace_files", "Lists files and folders within the workspace."),
                AIFunctionFactory.Create(GrepSearch, "grep_search", "Fast regex or text search across workspace files, returning line numbers and snippets."),
                AIFunctionFactory.Create(ReadFile, "read_file", "Reads file contents from disk with line numbering."),
                AIFunctionFactory.Create(WriteFile, "write_file", "Creates or overwrites a file in the workspace with given content."),
                AIFunctionFactory.Create(ModifyFile, "modify_file", "Modifies a file by replacing an exact, unique code snippet with new code."),
                AIFunctionFactory.Create(GetActiveFileContext, "get_active_file_context", "Gets the file name, active cursor position, selection, and code of the open editor document."),
                AIFunctionFactory.Create(ModifyActiveDocument, "modify_active_document", "Replaces the content of the currently open editor document with new code."),
                AIFunctionFactory.Create(InsertAtCursor, "insert_at_cursor", "Inserts code at the current caret position in the open document."),
                AIFunctionFactory.Create(GetDiagnostics, "get_diagnostics", "Retrieves active compiler errors and warnings from the Problems panel."),
                AIFunctionFactory.Create(CompileAndGetDiagnostics, "compile_and_get_diagnostics", "Compiles C# code using the in-memory Roslyn compiler and returns any diagnostic errors."),
                AIFunctionFactory.Create(RunCommand, "run_command", "Executes a shell command in the workspace."),
                AIFunctionFactory.Create(ApplyStudioCustomization, "apply_studio_customization", "Applies an init.csx customization script to dynamically alter the running studio.")
            };
        }

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(ListWorkspaceFiles, "list_workspace_files", "Lists files and folders within the workspace."),
            AIFunctionFactory.Create(GlobFiles, "glob_files", "Discovers files in the workspace matching a wildcard or glob pattern (e.g. **/*.cs, *.py)."),
            AIFunctionFactory.Create(GrepSearch, "grep_search", "Fast regex or text search across workspace files, returning line numbers and snippets."),
            AIFunctionFactory.Create(SearchCodebase, "search_codebase", "Searches the codebase for a text pattern or regex query."),
            AIFunctionFactory.Create(ReadFile, "read_file", "Reads file contents from disk with line numbering, optionally with startLine and endLine boundaries."),
            AIFunctionFactory.Create(WriteFile, "write_file", "Creates or overwrites a file in the workspace with given content."),
            AIFunctionFactory.Create(ModifyFile, "modify_file", "Modifies a file by replacing an exact, unique code snippet with new code."),
            AIFunctionFactory.Create(RunCommand, "run_command", "Executes a shell command (dotnet build, dotnet test, git, python, etc.) in the workspace."),
            AIFunctionFactory.Create(ReadProjectRules, "read_project_rules", "Reads workspace architectural rules from AGENTS.md, GEMINI.md, CLAUDE.md, or .cursorrules."),
            AIFunctionFactory.Create(GitStatus, "git_status", "Gets git status: branch, staged, unstaged, and untracked changes."),
            AIFunctionFactory.Create(GitDiff, "git_diff", "Gets git diff for a specific file or repository (staged or unstaged)."),
            AIFunctionFactory.Create(GetActiveFileContext, "get_active_file_context", "Gets the file name, active cursor position, selection, and code of the open editor document."),
            AIFunctionFactory.Create(ModifyActiveDocument, "modify_active_document", "Replaces the content of the currently open editor document."),
            AIFunctionFactory.Create(InsertAtCursor, "insert_at_cursor", "Inserts code at the current caret position in the open document."),
            AIFunctionFactory.Create(GetDiagnostics, "get_diagnostics", "Retrieves active compiler errors and warnings from the Problems panel."),
            AIFunctionFactory.Create(CompileAndGetDiagnostics, "compile_and_get_diagnostics", "Compiles C# code using the in-memory Roslyn compiler and returns any diagnostic errors."),
            AIFunctionFactory.Create(ApplyStudioCustomization, "apply_studio_customization", "Applies an init.csx customization script to dynamically alter the running studio (add Activity Bar icons, bottom tabs, or custom commands)."),
            AIFunctionFactory.Create(QueryStudioApiDocs, "query_studio_api_docs", "Searches studio API documentation, SDK guides, and extensibility manuals."),
            AIFunctionFactory.Create(GetStudioApiMetadata, "get_studio_api_metadata", "Retrieves structured API documentation and method signatures for App.UI, App.Theme, App.Commands, App.Editor, App.Hooks, and App.State."),
            AIFunctionFactory.Create(GetRuntimeExtensionPoints, "get_runtime_extension_points", "Discovers active UI contribution slots, registered commands, active theme tokens, and mounted items in the running application."),
            AIFunctionFactory.Create(InspectStudioUi, "inspect_studio_ui", "Inspects the live Avalonia visual tree of the running studio window, returning visible control hierarchy, names, types, and bounds."),
            AIFunctionFactory.Create(InjectStudioWidget, "inject_studio_widget", "Dynamically injects a custom widget or declarative XAML control into a studio slot ('EditorToolbar', 'FloatingOverlay', 'ComposerAction', 'BottomDeck', 'SideBar')."),
            AIFunctionFactory.Create(ConfigureAiAssistant, "configure_ai_assistant", "Configures AI assistant visual styling, opacity, persona, auto-accept diffs, or step limits."),
            AIFunctionFactory.Create(ExtractAiWindow, "extract_ai_window", "Extracts ('tears out') the AI assistant into an independent native desktop OS window for multi-monitor setups."),
            AIFunctionFactory.Create(DockAiAssistant, "dock_ai_assistant", "Docks the AI assistant back into the studio layout ('floating', 'sidebar', 'bottom')."),
            AIFunctionFactory.Create(TakeWorkspaceScreenshot, "take_workspace_screenshot", "Captures a clean screenshot of the studio workspace/editor canvas without AI self-occlusion."),
            AIFunctionFactory.Create(SetAiProtection, "set_ai_protection", "Configures OS-level window display affinity to protect the AI assistant and workspace from external screen recorders/sharing (Zoom, Teams, OBS).")
        };

        return tools;
    }

    [Description("Lists files in the workspace matching an optional glob or directory path.")]
    public string ListWorkspaceFiles(
        [Description("Subdirectory path to list, or empty for workspace root.")] string? directory = null,
        [Description("Optional search pattern (e.g. *.cs, *.py).")] string? pattern = null)
    {
        var root = _storageService?.ActiveWorkspaceRootPath ?? Directory.GetCurrentDirectory();
        var targetDir = string.IsNullOrWhiteSpace(directory) ? root : Path.Combine(root, directory);

        if (!Directory.Exists(targetDir)) return $"Directory not found: {targetDir}";

        var searchPattern = string.IsNullOrWhiteSpace(pattern) ? "*.*" : pattern;
        try
        {
            var files = Directory.EnumerateFiles(targetDir, searchPattern, SearchOption.TopDirectoryOnly)
                .Select(f => Path.GetRelativePath(root, f))
                .Take(100)
                .ToList();

            var dirs = Directory.EnumerateDirectories(targetDir)
                .Select(d => Path.GetRelativePath(root, d) + "/")
                .Take(50)
                .ToList();

            return $"Workspace root: {root}\nDirectories:\n{string.Join("\n", dirs)}\nFiles:\n{string.Join("\n", files)}";
        }
        catch (Exception ex)
        {
            return $"Error listing files: {ex.Message}";
        }
    }

    [Description("Searches the workspace for text matches or regular expressions.")]
    public string SearchCodebase(
        [Description("Search term or regex pattern.")] string query,
        [Description("Match case if true.")] bool caseSensitive = false)
    {
        var root = _storageService?.ActiveWorkspaceRootPath ?? Directory.GetCurrentDirectory();
        if (!Directory.Exists(root)) return "No active workspace folder.";

        var matches = new List<string>();
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                if (file.Contains("/.git/") || file.Contains("/bin/") || file.Contains("/obj/")) continue;

                var relPath = Path.GetRelativePath(root, file);
                int lineNum = 1;
                foreach (var line in File.ReadLines(file))
                {
                    if (line.Contains(query, comparison))
                    {
                        matches.Add($"{relPath}:{lineNum}: {line.Trim()}");
                        if (matches.Count >= 40) break;
                    }
                    lineNum++;
                }
                if (matches.Count >= 40) break;
            }

            return matches.Count > 0 ? string.Join("\n", matches) : $"No matches found for '{query}'.";
        }
        catch (Exception ex)
        {
            return $"Search error: {ex.Message}";
        }
    }

    [Description("Reads the text content of a file from disk with line numbering, optionally bounded by startLine and endLine.")]
    public string ReadFile(
        [Description("Relative or absolute file path.")] string path,
        [Description("Optional starting line number (1-based).")] int? startLine = null,
        [Description("Optional ending line number (1-based).")] int? endLine = null)
    {
        var fullPath = ResolvePath(path);
        if (!File.Exists(fullPath)) return $"File not found: {path}";

        try
        {
            var lines = File.ReadAllLines(fullPath);
            int start = Math.Max(1, startLine ?? 1);
            int end = Math.Min(lines.Length, endLine ?? (startLine.HasValue ? Math.Min(lines.Length, start + 250) : Math.Min(lines.Length, 300)));

            if (start > end) return $"Invalid line range: {start} to {end} (file has {lines.Length} lines).";

            var slice = lines.Skip(start - 1).Take(end - start + 1)
                .Select((line, idx) => $"{start + idx}: {line}");

            var result = string.Join("\n", slice);
            if (end < lines.Length && !endLine.HasValue)
            {
                result += $"\n[Showing lines {start} to {end} of {lines.Length}. Use startLine and endLine to inspect additional lines]";
            }

            return result;
        }
        catch (Exception ex)
        {
            return $"Error reading file: {ex.Message}";
        }
    }

    [Description("Creates or overwrites a file with given text content.")]
    public string WriteFile(
        [Description("Relative or absolute file path.")] string path,
        [Description("Content to write into the file.")] string content)
    {
        var fullPath = ResolvePath(path);
        try
        {
            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string original = File.Exists(fullPath) ? File.ReadAllText(fullPath) : string.Empty;
            File.WriteAllText(fullPath, content);

            var modifiedItem = new ModifiedFileItem
            {
                FilePath = fullPath,
                RelativePath = path,
                OriginalContent = original,
                ModifiedContent = content
            };
            modifiedItem.CalculateLineMetrics();
            _onFileModified?.Invoke(modifiedItem);

            return $"File written successfully ({content.Split('\n').Length} lines): {path}";
        }
        catch (Exception ex)
        {
            return $"Error writing file: {ex.Message}";
        }
    }

    [Description("Modifies an existing file by replacing an exact, unique snippet of code with new content.")]
    public string ModifyFile(
        [Description("Relative or absolute file path.")] string path,
        [Description("Exact character sequence to be replaced.")] string targetSnippet,
        [Description("Replacement character sequence.")] string replacementSnippet)
    {
        var fullPath = ResolvePath(path);
        if (!File.Exists(fullPath)) return $"File not found: {path}";

        try
        {
            string original = File.ReadAllText(fullPath);
            string normOriginal = original.Replace("\r\n", "\n");
            string normTarget = targetSnippet.Replace("\r\n", "\n");
            string normReplacement = replacementSnippet.Replace("\r\n", "\n");

            int firstIndex = normOriginal.IndexOf(normTarget, StringComparison.Ordinal);
            if (firstIndex < 0)
            {
                return $"Target snippet not found in file: {path}. Ensure the target matches existing text exactly including indentation and whitespace.";
            }

            int secondIndex = normOriginal.IndexOf(normTarget, firstIndex + normTarget.Length, StringComparison.Ordinal);
            if (secondIndex >= 0)
            {
                return $"Target snippet appears multiple times in {path}. Provide more surrounding lines of context to uniquely identify the block to replace.";
            }

            bool useCrLf = original.Contains("\r\n");
            string updatedNorm = normOriginal.Remove(firstIndex, normTarget.Length).Insert(firstIndex, normReplacement);
            string updated = useCrLf ? updatedNorm.Replace("\n", "\r\n") : updatedNorm;

            File.WriteAllText(fullPath, updated);

            var modifiedItem = new ModifiedFileItem
            {
                FilePath = fullPath,
                RelativePath = path,
                OriginalContent = original,
                ModifiedContent = updated
            };
            modifiedItem.CalculateLineMetrics();
            _onFileModified?.Invoke(modifiedItem);

            return $"Successfully modified {path} ({modifiedItem.SummaryText}).";
        }
        catch (Exception ex)
        {
            return $"Error modifying file: {ex.Message}";
        }
    }

    /// <summary>
    /// Returns the resolved file path or title of the currently active document.
    /// </summary>
    public string GetActiveDocumentPath() =>
        _codeStudioViewModel?.Script?.SourceFilePath ?? _codeStudioViewModel?.Script?.Title ?? "ActiveScript.cs";

    /// <summary>
    /// Returns the text content of the currently active document.
    /// </summary>
    public string GetActiveDocumentCode() =>
        _codeStudioViewModel?.Code ?? string.Empty;

    [Description("Gets context on the currently active file in the editor.")]
    public string GetActiveFileContext()
    {
        if (_codeStudioViewModel == null) return "No active editor instance.";

        var title = _codeStudioViewModel.Script?.Title ?? "Untitled";
        var path = _codeStudioViewModel.Script?.SourceFilePath ?? "Memory";
        var lang = _codeStudioViewModel.ActiveLanguage?.DisplayName ?? "C#";
        var line = _codeStudioViewModel.CaretLine;
        var col = _codeStudioViewModel.CaretColumn;
        var selection = _codeStudioViewModel.GetSelectedText?.Invoke() ?? string.Empty;
        var code = _codeStudioViewModel.Code;

        return $"""
            Active Document: {title} ({path})
            Language: {lang}
            Caret: Line {line}, Column {col}
            Selected Text: {(string.IsNullOrEmpty(selection) ? "<none>" : selection)}
            Document Text:
            {code}
            """;
    }

    [Description("Replaces the entire content of the active editor document with new content.")]
    public string ModifyActiveDocument(
        [Description("New text content for the active document.")] string newContent,
        [Description("Brief explanation of why the change was made.")] string explanation)
    {
        if (_codeStudioViewModel == null) return "No active editor instance.";

        string original = _codeStudioViewModel.Code;
        _codeStudioViewModel.Code = newContent;

        var path = _codeStudioViewModel.Script?.SourceFilePath ?? _codeStudioViewModel.Script?.Title ?? "ActiveScript.cs";
        var modifiedItem = new ModifiedFileItem
        {
            FilePath = path,
            RelativePath = Path.GetFileName(path),
            OriginalContent = original,
            ModifiedContent = newContent
        };
        modifiedItem.CalculateLineMetrics();
        _onFileModified?.Invoke(modifiedItem);

        return $"Active document updated ({modifiedItem.SummaryText}): {explanation}";
    }

    [Description("Inserts code at the current editor caret position.")]
    public string InsertAtCursor([Description("Text to insert.")] string text)
    {
        if (_codeStudioViewModel == null) return "No active editor instance.";

        if (_codeStudioViewModel.InsertEditorText != null)
        {
            _codeStudioViewModel.InsertEditorText.Invoke(text);
            return "Inserted text at cursor.";
        }

        _codeStudioViewModel.Code += text;
        return "Appended text to document.";
    }

    [Description("Gets all active compiler errors and warnings from the Problems panel.")]
    public string GetDiagnostics()
    {
        if (_codeStudioViewModel == null || _codeStudioViewModel.Diagnostics.Count == 0)
        {
            return "No problems detected.";
        }

        var list = _codeStudioViewModel.Diagnostics
            .Select(d => $"[{d.Severity}] {d.Id} at Ln {d.Line}, Col {d.Column}: {d.Message}");

        return string.Join("\n", list);
    }

    [Description("Compiles C# code with Roslyn and returns any compilation errors or warnings.")]
    public string CompileAndGetDiagnostics([Description("C# source code to compile and check.")] string csharpCode)
    {
        var diagnostics = _compilerService?.CheckDiagnostics(csharpCode) ?? Array.Empty<DiagnosticItem>();
        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

        if (errors.Count == 0)
        {
            return "Compilation succeeded with 0 errors.";
        }

        var lines = errors.Select(e => $"Error {e.Id} at Ln {e.Line}, Col {e.Column}: {e.Message}");
        return $"Compilation failed with {errors.Count} error(s):\n" + string.Join("\n", lines);
    }

    [Description("Applies a C# customization script (init.csx) to dynamically modify the running studio.")]
    public async Task<string> ApplyStudioCustomization(
        [Description("C# script code to apply as studio customization.")] string initCsxContent)
    {
        if (_customizationManager == null)
        {
            return "Customization manager is not available in current host.";
        }

        try
        {
            await _customizationManager.Storage.SaveInitScriptAsync(initCsxContent);
            var result = await _customizationManager.Session.ApplyScriptAsync(initCsxContent);

            if (result.Success)
            {
                return "Customization applied successfully! Studio UI and hooks reloaded.";
            }

            return $"Customization error: {result.ErrorMessage}";
        }
        catch (Exception ex)
        {
            return $"Failed to apply studio customization: {ex.Message}";
        }
    }

    [Description("Searches studio API documentation and tutorials for SDK methods and usage.")]
    public string QueryStudioApiDocs([Description("Search query.")] string query)
    {
        var articles = _documentationService?.SearchArticles(query) ?? new List<DocArticle>();
        if (articles.Count == 0) return $"No documentation articles found for '{query}'.";

        var sb = new System.Text.StringBuilder();
        foreach (var art in articles.Take(3))
        {
            sb.AppendLine($"# {art.Title} - {art.Subtitle}");
            sb.AppendLine(art.Summary);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        var root = _storageService?.ActiveWorkspaceRootPath ?? Directory.GetCurrentDirectory();
        return Path.Combine(root, path);
    }
}
