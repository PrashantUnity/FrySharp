using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FryWorld.Sdk.AI;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.Common;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.Notebooks;

namespace PdfEditorApp.Plugins.CSharpEditor.Services.AI.Providers;

/// <summary>
/// Provides live script, compiler diagnostic, and workspace context from FrySharp to FryWorld AI and MCP clients.
/// </summary>
public class CSharpAiContextProvider : IAiContextProvider
{
    private readonly Func<CSharpStudioHostViewModel?> _getHostVm;

    public string PluginId => "com.fryworld.csharpstudio";

    public CSharpAiContextProvider(Func<CSharpStudioHostViewModel?> getHostVm)
    {
        _getHostVm = getHostVm ?? throw new ArgumentNullException(nameof(getHostVm));
    }

    public Task<AiContextSnapshot> GetCurrentContextAsync(CancellationToken cancellationToken = default)
    {
        var host = _getHostVm();
        var page = host?.CurrentPage;

        string title = "FrySharp Developer Studio";
        string? docName = null;
        string? docPath = null;
        string mimeType = "text/x-csharp";
        string activeContent = string.Empty;
        var metadata = new Dictionary<string, string>();

        if (page is CSharpCodeStudioViewModel codeVm)
        {
            var script = codeVm.Script;
            docName = script?.Title ?? "Script.csx";
            docPath = script?.SourceFilePath;
            activeContent = codeVm.Code ?? string.Empty;
            title = $"FrySharp Code Studio ({docName})";

            var langId = script?.LanguageId ?? "csharp";
            mimeType = langId switch
            {
                "python" => "text/x-python",
                "javascript" => "text/javascript",
                "rust" => "text/x-rust",
                "go" => "text/x-go",
                "fsharp" => "text/x-fsharp",
                "cpp" => "text/x-c++src",
                _ => "text/x-csharp"
            };

            metadata["Language"] = langId;
            metadata["IsExecuting"] = codeVm.IsExecuting.ToString();
            metadata["IsPaused"] = codeVm.IsPaused.ToString();
            metadata["IsDebugging"] = codeVm.IsDebugging.ToString();

            if (langId == "csharp" && codeVm.CompilerService != null)
            {
                try
                {
                    var diagnostics = codeVm.CompilerService.CheckDiagnostics(activeContent);
                    var errors = diagnostics.Count(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
                    var warnings = diagnostics.Count(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
                    metadata["CompilerErrors"] = errors.ToString();
                    metadata["CompilerWarnings"] = warnings.ToString();
                    if (errors > 0)
                    {
                        var topErrors = diagnostics
                            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                            .Take(3)
                            .Select(d => $"L{d.Line}: {d.Message}");
                        metadata["TopErrors"] = string.Join(" | ", topErrors);
                    }
                }
                catch
                {
                    // Ignore diagnostics analysis failure during live edits
                }
            }
        }
        else if (page is CSharpNotebookStudioViewModel nbVm)
        {
            var doc = nbVm.Notebook;
            docName = doc?.Title ?? "Notebook.ipynb";
            docPath = nbVm.ActiveTab?.FilePath;
            title = $"FrySharp Notebook ({docName})";
            mimeType = "application/x-ipynb+json";

            var activeCell = nbVm.ActiveCell;
            activeContent = activeCell?.Source ?? string.Empty;

            metadata["DocumentType"] = "Notebook";
            metadata["CellCount"] = (doc?.Cells.Count ?? 0).ToString();
            metadata["IsExecuting"] = nbVm.IsExecuting.ToString();
            metadata["ActiveCellLanguage"] = activeCell?.Language ?? "csharp";
        }
        else
        {
            title = $"FrySharp ({host?.ActiveDocumentTitle ?? "Overview"})";
            mimeType = "text/plain";
            metadata["StudioPage"] = host?.ActiveDocumentTitle ?? "Hub";
        }

        const string systemPromptAugmentation =
            "FrySharp is an interactive C# and polyglot scripting development studio for the FryWorld platform.\n" +
            "Scripts run in-process with access to rich desktop display APIs:\n" +
            "- `Display(object)` / `o.Display()` renders rich interactive tables, cards, charts, and visualizations.\n" +
            "- `Dump(object)` / `o.Dump()` formats objects with detailed property graphs and JSON/tree inspector.\n" +
            "- `Check.That(...)` executes fluent assertion testing.\n" +
            "- Top-level statements, C# 13 features, Roslyn scripting, and LINQ are fully enabled.\n" +
            "- External NuGet packages and assemblies can be referenced using `#r \"nuget: PackageName, Version\"`.";

        var snapshot = new AiContextSnapshot
        {
            PluginId = PluginId,
            Title = title,
            ActiveDocumentName = docName,
            ActiveDocumentPath = docPath,
            MimeType = mimeType,
            ActiveContent = activeContent,
            SystemPromptAugmentation = systemPromptAugmentation,
            Metadata = metadata
        };

        return Task.FromResult(snapshot);
    }
}
