using CommunityToolkit.Mvvm.Input;
using PdfEditorApp.Plugins.CSharpEditor.Models;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.Notebooks;

public partial class CSharpNotebookStudioViewModel
{
    // ── Cell execution commands ───────────────────────────────────────────────

    [RelayCommand]
    public async Task RunSingleCellAsync(NotebookCellViewModel cell)
    {
        if (ActiveTab != null)
        {
            await ActiveTab.RunSingleCellAsync(cell);
        }
    }

    [RelayCommand]
    public async Task RunCellAndSelectNextAsync(NotebookCellViewModel? cell = null)
    {
        if (ActiveTab != null)
        {
            await ActiveTab.RunCellAndSelectNextAsync(cell);
        }
    }

    [RelayCommand]
    public async Task RunAllCellsAsync()
    {
        if (ActiveTab != null)
        {
            await ActiveTab.RunAllCellsAsync();
        }
    }

    [RelayCommand]
    public void RestartKernel()
    {
        ActiveTab?.RestartKernel();
    }

    [RelayCommand]
    public void InterruptExecution()
    {
        ActiveTab?.InterruptExecution();
    }

    // ── Cell management commands ──────────────────────────────────────────────

    [RelayCommand]
    public void SelectCell(NotebookCellViewModel? cell)
    {
        ActiveTab?.SelectCell(cell);
    }

    [RelayCommand]
    public void AddCodeCell(NotebookCellViewModel? afterCell = null)
    {
        ActiveTab?.AddCodeCell(afterCell);
    }

    [RelayCommand]
    public void AddMarkdownCell(NotebookCellViewModel? afterCell = null)
    {
        ActiveTab?.AddMarkdownCell(afterCell);
    }

    [RelayCommand]
    public void InsertCodeCellAfter(NotebookCellViewModel? cell)
    {
        ActiveTab?.AddCodeCell(cell);
    }

    [RelayCommand]
    public void InsertMarkdownCellAfter(NotebookCellViewModel? cell)
    {
        ActiveTab?.AddMarkdownCell(cell);
    }

    [RelayCommand]
    public void AddCellAbove()
    {
        ActiveTab?.AddCellAbove(ActiveTab.ActiveCell, CellType.Code);
    }

    [RelayCommand]
    public void DeleteActiveCell()
    {
        if (ActiveTab?.ActiveCell != null)
        {
            ActiveTab.DeleteCell(ActiveTab.ActiveCell);
        }
    }

    [RelayCommand]
    public void SelectCellFromOutline(NotebookCellViewModel? cell)
    {
        if (cell == null || ActiveTab == null) return;
        ActiveTab.SelectCell(cell);
        RequestScrollToCell?.Invoke(cell);
    }

    // ── Cell visibility commands ──────────────────────────────────────────────

    [RelayCommand]
    public void ClearAllOutputs()
    {
        ActiveTab?.ClearAllOutputs();
    }

    [RelayCommand]
    public void CollapseAllInputs()
    {
        ActiveTab?.CollapseAllInputs();
    }

    [RelayCommand]
    public void ExpandAllInputs()
    {
        ActiveTab?.ExpandAllInputs();
    }

    [RelayCommand]
    public void CollapseAllOutputs()
    {
        ActiveTab?.CollapseAllOutputs();
    }

    [RelayCommand]
    public void ExpandAllOutputs()
    {
        ActiveTab?.ExpandAllOutputs();
    }

    [RelayCommand]
    public void CollapseAllCells()
    {
        ActiveTab?.CollapseAllCells();
    }

    [RelayCommand]
    public void ExpandAllCells()
    {
        ActiveTab?.ExpandAllCells();
    }

    [RelayCommand]
    public void FoldAllCodeBlocks()
    {
        ActiveTab?.FoldAllCodeBlocks();
    }

    [RelayCommand]
    public void UnfoldAllCodeBlocks()
    {
        ActiveTab?.UnfoldAllCodeBlocks();
    }

    [RelayCommand]
    public void FormatAllCodeCells()
    {
        ActiveTab?.FormatAllCodeCells();
    }

    // ── Save ─────────────────────────────────────────────────────────────────

    [RelayCommand]
    public async Task SaveAsync()
    {
        if (ActiveTab != null) await SaveTabAsync(ActiveTab);
    }

    /// <summary>
    /// Save As: writes the active notebook to <paramref name="filePath"/> (as Jupyter for .ipynb, as FrySharp for .frynb and
    /// .csnb) and makes that file the tab's. False when it couldn't be written.
    /// </summary>
    public async Task<bool> SaveActiveNotebookAsAsync(string filePath)
    {
        if (ActiveTab is not { } tab) return false;
        var written = await _storageService.SaveNotebookAsAsync(tab.Notebook, filePath);
        if (written == null)
        {
            CompilerStatusText = $"⚠️ Couldn't save {Path.GetFileName(filePath)}: check the folder's permissions";
            return false;
        }

        tab.FilePath = written;
        tab.Title = Path.GetFileName(written);
        tab.IsModified = false;
        CompilerStatusText = "Saved";
        return true;
    }

    /// <summary>Saves <paramref name="tab"/>'s notebook if it has edits (or was never saved). False when the save failed.</summary>
    private async Task<bool> SaveTabAsync(NotebookTabViewModel tab)
    {
        // An ephemeral notebook must be saved even when not yet modified (user pressed Ctrl+S on a fresh tab).
        if (!tab.IsModified && !tab.Notebook.IsEphemeral) return true;

        tab.Notebook.IsEphemeral = false;
        tab.Notebook.LastModified = DateTime.UtcNow;
        var saved = await _storageService.SaveNotebookAsync(tab.Notebook);
        tab.IsModified = !saved;
        CompilerStatusText = saved ? "Saved" : "⚠️ Save failed — check disk space/permissions";
        return saved;
    }
}
