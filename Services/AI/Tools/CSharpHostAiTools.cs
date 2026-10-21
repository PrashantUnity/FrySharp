using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Threading;
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

    [Description("Gets the file name, programming language, and complete source code of the active editor document or notebook cell in FrySharp.")]
    public string GetActiveCode()
    {
        var host = _getHostVm();
        if (host?.CurrentPage is CSharpCodeStudioViewModel codeVm)
        {
            var res = new
            {
                Type = "Script",
                Title = codeVm.Script?.Title ?? "Untitled.csx",
                Language = codeVm.Script?.LanguageId ?? "csharp",
                FilePath = codeVm.Script?.SourceFilePath,
                Code = codeVm.Code ?? string.Empty,
                IsExecuting = codeVm.IsExecuting,
                IsPaused = codeVm.IsPaused
            };
            return JsonSerializer.Serialize(res);
        }
        if (host?.CurrentPage is CSharpNotebookStudioViewModel nbVm)
        {
            var res = new
            {
                Type = "Notebook",
                Title = nbVm.Notebook?.Title ?? "Notebook.ipynb",
                Language = nbVm.ActiveCell?.Language ?? "csharp",
                FilePath = nbVm.ActiveTab?.FilePath,
                Code = nbVm.ActiveCell?.Source ?? string.Empty,
                IsExecuting = nbVm.IsExecuting
            };
            return JsonSerializer.Serialize(res);
        }

        return JsonSerializer.Serialize(new { Status = "No active document or script open in FrySharp" });
    }

    [Description("Analyzes the active C# code using Roslyn compiler diagnostics and returns syntax and semantic errors and warnings.")]
    public string GetDiagnostics()
    {
        var host = _getHostVm();
        if (host?.CurrentPage is CSharpCodeStudioViewModel codeVm)
        {
            if (codeVm.CompilerService == null)
            {
                return JsonSerializer.Serialize(new { Error = "CompilerService is not available" });
            }

            var diagnostics = codeVm.CompilerService.CheckDiagnostics(codeVm.Code ?? string.Empty);
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
        var host = _getHostVm();
        if (host?.CurrentPage is CSharpCodeStudioViewModel codeVm)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                codeVm.Code = newCode;
            });
            return JsonSerializer.Serialize(new { Success = true, Message = "Active script code updated successfully." });
        }
        if (host?.CurrentPage is CSharpNotebookStudioViewModel nbVm && nbVm.ActiveCell != null)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                nbVm.ActiveCell.Source = newCode;
            });
            return JsonSerializer.Serialize(new { Success = true, Message = "Active notebook cell code updated successfully." });
        }

        return JsonSerializer.Serialize(new { Success = false, Message = "No active editor document or notebook cell found." });
    }

    [Description("Executes the active script or notebook cell in FrySharp.")]
    public async Task<string> RunScript()
    {
        _navigateToStudio?.Invoke();
        var host = _getHostVm();
        if (host?.CurrentPage is CSharpCodeStudioViewModel codeVm)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (codeVm.IsPaused)
                {
                    codeVm.ContinueDebug();
                }
                else if (!codeVm.IsExecuting && !codeVm.IsDebugging)
                {
                    codeVm.DebugCodeCommand.Execute(null);
                }
            });
            return JsonSerializer.Serialize(new { Success = true, Message = "Execution started in Code Studio." });
        }
        if (host?.CurrentPage is CSharpNotebookStudioViewModel nbVm)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                nbVm.RunCellAndSelectNextCommand.Execute(null);
            });
            return JsonSerializer.Serialize(new { Success = true, Message = "Execution started in Notebook Studio." });
        }

        return JsonSerializer.Serialize(new { Success = false, Message = "No runnable script or notebook is currently open." });
    }

    [Description("Halts or interrupts active execution in FrySharp.")]
    public async Task<string> StopScript()
    {
        var host = _getHostVm();
        if (host?.CurrentPage is CSharpCodeStudioViewModel codeVm)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (codeVm.IsDebugging) codeVm.StopDebug();
                else codeVm.StopCommand.Execute(null);
            });
            return JsonSerializer.Serialize(new { Success = true, Message = "Execution stopped in Code Studio." });
        }
        if (host?.CurrentPage is CSharpNotebookStudioViewModel nbVm)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                nbVm.InterruptExecutionCommand.Execute(null);
            });
            return JsonSerializer.Serialize(new { Success = true, Message = "Execution interrupted in Notebook Studio." });
        }

        return JsonSerializer.Serialize(new { Success = false, Message = "No running execution found to stop." });
    }
}
