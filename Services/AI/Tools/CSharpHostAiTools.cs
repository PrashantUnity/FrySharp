using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
using PdfEditorApp.Plugins.CSharpEditor.Models;
using PdfEditorApp.Plugins.CSharpEditor.Services.Execution;
using PdfEditorApp.Plugins.CSharpEditor.Services.Roslyn;
using PdfEditorApp.Plugins.CSharpEditor.Services.Storage;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.CodeStudio;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.Common;
using PdfEditorApp.Plugins.CSharpEditor.ViewModels.Notebooks;

namespace PdfEditorApp.Plugins.CSharpEditor.Services.AI.Tools;

/// <summary>
/// Exposes FrySharp studio capabilities as callable AI tools for the FryWorld host assistant and MCP clients.
/// </summary>
public class CSharpHostAiTools
{
    private readonly Func<CSharpStudioHostViewModel?> _getHostVm;
    private readonly Action? _navigateToStudio;

    public CSharpHostAiTools(Func<CSharpStudioHostViewModel?> getHostVm, Action? navigateToStudio = null)
    {
        _getHostVm = getHostVm ?? throw new ArgumentNullException(nameof(getHostVm));
        _navigateToStudio = navigateToStudio;
    }

    private CSharpStudioHostViewModel? GetVm() => _getHostVm();

    private static async Task RunOnUiAsync(Action action)
    {
        if (Avalonia.Application.Current == null || Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            await Dispatcher.UIThread.InvokeAsync(action);
        }
    }

    [Description("Gets the file name, programming language, and complete source code of the active editor document or notebook cell in FrySharp.")]
    public string GetActiveCode()
    {
        var host = GetVm();
        if (host?.CurrentPage is CSharpCodeStudioViewModel activeCode)
        {
            return FormatCodeStudio(activeCode);
        }
        if (host?.CurrentPage is CSharpNotebookStudioViewModel activeNb)
        {
            return FormatNotebookStudio(activeNb);
        }
        if (host?.CodeStudioViewModel != null && !string.IsNullOrWhiteSpace(host.CodeStudioViewModel.Code))
        {
            return FormatCodeStudio(host.CodeStudioViewModel);
        }
        if (host?.NotebookStudioViewModel?.ActiveCell != null)
        {
            return FormatNotebookStudio(host.NotebookStudioViewModel);
        }

        return JsonSerializer.Serialize(new { Status = "No active document or script open in FrySharp" });

        static string FormatCodeStudio(CSharpCodeStudioViewModel vm) => JsonSerializer.Serialize(new
        {
            Type = "Script",
            Title = vm.Script?.Title ?? "Untitled.csx",
            Language = vm.Script?.LanguageId ?? "csharp",
            FilePath = vm.Script?.SourceFilePath,
            Code = vm.Code ?? string.Empty,
            IsExecuting = vm.IsExecuting,
            IsPaused = vm.IsPaused
        });

        static string FormatNotebookStudio(CSharpNotebookStudioViewModel vm) => JsonSerializer.Serialize(new
        {
            Type = "Notebook",
            Title = vm.Notebook?.Title ?? "Notebook.ipynb",
            Language = vm.ActiveCell?.Language ?? "csharp",
            FilePath = vm.ActiveTab?.FilePath,
            Code = vm.ActiveCell?.Source ?? string.Empty,
            IsExecuting = vm.IsExecuting
        });
    }

    [Description("Analyzes C# code using Roslyn compiler diagnostics and returns syntax and semantic errors and warnings. If code is omitted, the active studio document is analyzed.")]
    public string GetDiagnostics([Description("Optional C# source code to analyze. If omitted, the active studio document is analyzed.")] string? code = null)
    {
        var host = GetVm();

        if (!string.IsNullOrWhiteSpace(code))
        {
            var compiler = host?.CodeStudioViewModel?.CompilerService ?? new RoslynCompilerService();
            var diagnostics = compiler.CheckDiagnostics(code);
            var items = diagnostics.Select(d => new
            {
                Id = d.Id,
                Severity = d.Severity.ToString(),
                Message = d.Message,
                Line = d.Line,
                Column = d.Column
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                TotalCount = items.Count,
                ErrorCount = items.Count(d => d.Severity == "Error"),
                WarningCount = items.Count(d => d.Severity == "Warning"),
                Diagnostics = items
            });
        }

        var codeVm = (host?.CurrentPage as CSharpCodeStudioViewModel) ?? host?.CodeStudioViewModel;
        if (codeVm != null)
        {
            var compiler = codeVm.CompilerService ?? new RoslynCompilerService();
            var diagnostics = compiler.CheckDiagnostics(codeVm.Code ?? string.Empty);
            var items = diagnostics.Select(d => new
            {
                Id = d.Id,
                Severity = d.Severity.ToString(),
                Message = d.Message,
                Line = d.Line,
                Column = d.Column
            }).ToList();

            return JsonSerializer.Serialize(new
            {
                TotalCount = items.Count,
                ErrorCount = items.Count(d => d.Severity == "Error"),
                WarningCount = items.Count(d => d.Severity == "Warning"),
                Diagnostics = items
            });
        }

        return JsonSerializer.Serialize(new { Status = "Active document is not a C# Code Studio script" });
    }

    [Description("Updates or replaces the code content in the active FrySharp editor document or notebook cell.")]
    public async Task<string> SetActiveCode([Description("The new code to set in the editor")] string newCode)
    {
        var host = GetVm();
        if (host?.CurrentPage is CSharpCodeStudioViewModel codeVm)
        {
            await RunOnUiAsync(() =>
            {
                codeVm.Code = newCode;
            });
            return JsonSerializer.Serialize(new { Success = true, Message = "Active script code updated successfully." });
        }
        if (host?.CurrentPage is CSharpNotebookStudioViewModel nbVm && nbVm.ActiveCell != null)
        {
            await RunOnUiAsync(() =>
            {
                nbVm.ActiveCell.Source = newCode;
            });
            return JsonSerializer.Serialize(new { Success = true, Message = "Active notebook cell code updated successfully." });
        }
        if (host?.CodeStudioViewModel != null)
        {
            await RunOnUiAsync(() =>
            {
                host.CodeStudioViewModel.Code = newCode;
            });
            return JsonSerializer.Serialize(new { Success = true, Message = "Script code updated successfully." });
        }
        if (host?.NotebookStudioViewModel?.ActiveCell != null)
        {
            await RunOnUiAsync(() =>
            {
                host.NotebookStudioViewModel.ActiveCell.Source = newCode;
            });
            return JsonSerializer.Serialize(new { Success = true, Message = "Notebook cell code updated successfully." });
        }

        return JsonSerializer.Serialize(new { Success = false, Message = "No active editor document or notebook cell found." });
    }

    [Description("Executes the active script or notebook cell in FrySharp.")]
    public async Task<string> RunScript()
    {
        _navigateToStudio?.Invoke();
        var host = GetVm();
        if (host?.CurrentPage is CSharpCodeStudioViewModel activeCode)
        {
            await RunOnUiAsync(() => TriggerCodeRun(activeCode));
            return JsonSerializer.Serialize(new { Success = true, Message = "Execution started in Code Studio." });
        }
        if (host?.CurrentPage is CSharpNotebookStudioViewModel activeNb)
        {
            await RunOnUiAsync(() => activeNb.RunCellAndSelectNextCommand.Execute(null));
            return JsonSerializer.Serialize(new { Success = true, Message = "Execution started in Notebook Studio." });
        }
        if (host?.CodeStudioViewModel != null)
        {
            await RunOnUiAsync(() => TriggerCodeRun(host.CodeStudioViewModel));
            return JsonSerializer.Serialize(new { Success = true, Message = "Execution started in Code Studio." });
        }
        if (host?.NotebookStudioViewModel != null)
        {
            await RunOnUiAsync(() => host.NotebookStudioViewModel.RunCellAndSelectNextCommand.Execute(null));
            return JsonSerializer.Serialize(new { Success = true, Message = "Execution started in Notebook Studio." });
        }

        return JsonSerializer.Serialize(new { Success = false, Message = "No runnable script or notebook is currently open." });

        static void TriggerCodeRun(CSharpCodeStudioViewModel vm)
        {
            if (vm.IsPaused)
            {
                vm.ContinueDebug();
            }
            else if (!vm.IsExecuting && !vm.IsDebugging)
            {
                vm.DebugCodeCommand.Execute(null);
            }
        }
    }

    [Description("Halts or interrupts active execution in FrySharp.")]
    public async Task<string> StopScript()
    {
        var host = GetVm();
        var codeVm = (host?.CurrentPage as CSharpCodeStudioViewModel) ?? host?.CodeStudioViewModel;
        if (codeVm != null && (codeVm.IsExecuting || codeVm.IsDebugging))
        {
            await RunOnUiAsync(() =>
            {
                if (codeVm.IsDebugging) codeVm.StopDebug();
                else codeVm.StopCommand.Execute(null);
            });
            return JsonSerializer.Serialize(new { Success = true, Message = "Execution stopped in Code Studio." });
        }
        var nbVm = (host?.CurrentPage as CSharpNotebookStudioViewModel) ?? host?.NotebookStudioViewModel;
        if (nbVm != null && nbVm.IsExecuting)
        {
            await RunOnUiAsync(() =>
            {
                nbVm.InterruptExecutionCommand.Execute(null);
            });
            return JsonSerializer.Serialize(new { Success = true, Message = "Execution interrupted in Notebook Studio." });
        }

        return JsonSerializer.Serialize(new { Success = false, Message = "No running execution found to stop." });
    }

    [Description("Returns the latest execution console output, status, and compiler diagnostics from the active FrySharp editor or notebook.")]
    public string GetExecutionOutput()
    {
        var host = GetVm();
        if (host?.CurrentPage is CSharpCodeStudioViewModel codeVm)
        {
            return FormatCodeStudio(codeVm);
        }
        if (host?.CurrentPage is CSharpNotebookStudioViewModel nbVm)
        {
            return FormatNotebookStudio(nbVm);
        }
        if (host?.CodeStudioViewModel != null)
        {
            return FormatCodeStudio(host.CodeStudioViewModel);
        }
        if (host?.NotebookStudioViewModel != null)
        {
            return FormatNotebookStudio(host.NotebookStudioViewModel);
        }

        return JsonSerializer.Serialize(new { Status = "No active script or notebook open" });

        static string FormatCodeStudio(CSharpCodeStudioViewModel vm) => JsonSerializer.Serialize(new
        {
            Type = "Script",
            IsExecuting = vm.IsExecuting,
            IsDebugging = vm.IsDebugging,
            IsPaused = vm.IsPaused,
            CompilerStatus = vm.CompilerStatusText,
            Output = vm.ConsoleOutput ?? string.Empty
        });

        static string FormatNotebookStudio(CSharpNotebookStudioViewModel vm)
        {
            var activeCell = vm.ActiveCell;
            return JsonSerializer.Serialize(new
            {
                Type = "Notebook",
                IsExecuting = vm.IsExecuting,
                CellId = activeCell?.Id,
                Language = activeCell?.Language,
                OutputCount = activeCell?.AvailableOutputCount ?? 0,
                Output = activeCell?.OutputText ?? string.Empty
            });
        }
    }

    [Description("Executes C# code directly in-memory using Roslyn and returns console output, compilation diagnostics, and execution telemetry.")]
    public async Task<string> ExecuteCode(
        [Description("The C# source code (top-level statements, expressions, or full classes) to compile and execute.")] string code,
        [Description("Execution mode: 'Statements' (default), 'Expression', or 'Program'.")] string mode = "Statements")
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return JsonSerializer.Serialize(new { Success = false, Error = "Code cannot be empty." });
        }

        var langMode = mode.Equals("Expression", StringComparison.OrdinalIgnoreCase)
            ? ExecutionLanguageMode.Expression
            : (mode.Equals("Program", StringComparison.OrdinalIgnoreCase) ? ExecutionLanguageMode.Program : ExecutionLanguageMode.Statements);

        var compiler = new RoslynCompilerService();
        var (compileSuccess, assemblyBytes, diagnostics) = compiler.CompileToAssembly(code, langMode);

        var errors = diagnostics.Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
        if (!compileSuccess || assemblyBytes == null || errors.Count > 0)
        {
            return JsonSerializer.Serialize(new
            {
                Success = false,
                Phase = "Compilation",
                ErrorCount = errors.Count,
                Diagnostics = diagnostics.Select(d => new { d.Id, Severity = d.Severity.ToString(), d.Message, d.Line, d.Column })
            });
        }

        var engine = new ScriptExecutionEngine();
        var capturedOutput = new StringBuilder();
        var execResult = await engine.ExecuteAsync(assemblyBytes, text => capturedOutput.Append(text));

        return JsonSerializer.Serialize(new
        {
            Success = execResult.Success,
            Phase = "Execution",
            Output = (capturedOutput.Length > 0 ? capturedOutput.ToString() : execResult.Output).TrimEnd(),
            Error = execResult.Error,
            DurationMs = execResult.Elapsed.TotalMilliseconds,
            WasCancelled = execResult.WasCancelled
        });
    }

    [Description("Creates a new C# automation script in FrySharp and opens it in Code Studio.")]
    public async Task<string> NewScript(
        [Description("Optional title for the new script (defaults to Script.csx).")] string? title = null,
        [Description("Initial C# code content for the script.")] string? initialCode = null)
    {
        _navigateToStudio?.Invoke();
        var host = GetVm();
        if (host == null)
        {
            return JsonSerializer.Serialize(new { Success = false, Message = "Studio host is not available." });
        }

        string docTitle = string.IsNullOrWhiteSpace(title) ? "Script.csx" : title;
        var scriptItem = new ScriptDocumentItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = docTitle,
            LanguageId = "csharp",
            Code = initialCode ?? "// New automation script\nConsole.WriteLine(\"Hello from FrySharp!\");\n"
        };

        await RunOnUiAsync(() =>
        {
            host.NavigateToCodeStudio(scriptItem);
        });

        return JsonSerializer.Serialize(new
        {
            Success = true,
            Title = scriptItem.Title,
            Language = scriptItem.LanguageId,
            Message = "Created and opened script in Code Studio."
        });
    }

    [Description("Opens a script or notebook file from disk into FrySharp Code Studio or Notebook Studio.")]
    public async Task<string> OpenFile([Description("Absolute file path to open in FrySharp.")] string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return JsonSerializer.Serialize(new { Success = false, Message = $"File not found: {filePath}" });
        }

        _navigateToStudio?.Invoke();
        var host = GetVm();
        if (host == null)
        {
            return JsonSerializer.Serialize(new { Success = false, Message = "Studio host is not available." });
        }

        if (NotebookFileFormat.IsNotebookPath(filePath))
        {
            var text = await File.ReadAllTextAsync(filePath);
            var notebook = NotebookFileFormat.Read(filePath, text, host.Languages.Registry);
            await RunOnUiAsync(() =>
            {
                host.NavigateToNotebookStudio(notebook);
            });
            return JsonSerializer.Serialize(new { Success = true, Type = "Notebook", FilePath = filePath, Title = notebook.Title });
        }

        var content = await File.ReadAllTextAsync(filePath);
        var lang = host.Languages.Registry.FindByExtension(filePath);
        var item = new ScriptDocumentItem
        {
            Id = Path.GetFileName(filePath),
            Title = Path.GetFileName(filePath),
            SourceFilePath = filePath,
            LanguageId = lang?.Id ?? "csharp",
            Code = content
        };

        await RunOnUiAsync(() =>
        {
            host.NavigateToCodeStudio(item);
        });

        return JsonSerializer.Serialize(new { Success = true, Type = "Script", FilePath = filePath, Language = item.LanguageId });
    }
}
