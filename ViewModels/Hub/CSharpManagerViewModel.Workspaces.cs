using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PdfEditorApp.Plugins.CSharpEditor.Models;

namespace PdfEditorApp.Plugins.CSharpEditor.ViewModels.Hub;

public partial class CSharpManagerViewModel
{
    public ObservableCollection<RecentWorkspaceItem> RecentWorkspaces { get; } = new();
    public ObservableCollection<RecentWorkspaceItem> FilteredWorkspaces { get; } = new();

    public int TotalRecentWorkspaces => RecentWorkspaces.Count;
    public int RecentWorkspacesCount => RecentWorkspaces.Count(w => w.IsProjectWorkspace);
    public int RecentNotebooksCount => RecentWorkspaces.Count(w => w.IsNotebook);
    public int RecentScriptsCount => RecentWorkspaces.Count(w => w.IsScript);
    public int RecentPinnedCount => RecentWorkspaces.Count(w => w.IsPinned);
    public bool HasFilteredWorkspaces => FilteredWorkspaces.Count > 0;

    /// <summary>
    /// A kind is picked in the side bar (Scripts, Notebooks, API Servers, Pinned): the list shows the library's documents of
    /// that kind (what the side bar's counts count), not the recently opened workspaces.
    /// </summary>
    public bool IsLibraryListVisible => SelectedTypeFilter is "Scripts" or "Notebooks" or "Servers" or "Pinned";

    public bool ShowRecentWorkspaces => !IsLibraryListVisible && HasFilteredWorkspaces;
    public bool ShowRecentEmptyState => !IsLibraryListVisible && !HasFilteredWorkspaces;
    public bool IsRecentWorkspacesEmpty => RecentWorkspaces.Count == 0;

    public bool IsWorkspacesFilterActive => SelectedTypeFilter is "Workspaces" or "Workspace";

    public async Task LoadRecentWorkspacesAsync()
    {
        try
        {
            var recents = await _storageService.RecentWorkspaces.LoadRecentWorkspacesAsync();

            // Auto-seed active workspace if recent list is empty on first launch
            if (recents.Count == 0 &&
                !string.IsNullOrWhiteSpace(_storageService.ActiveWorkspaceRootPath) &&
                Directory.Exists(_storageService.ActiveWorkspaceRootPath))
            {
                var seed = await _storageService.RecentWorkspaces.RecordWorkspaceOpenedAsync(
                    _storageService.ActiveWorkspaceRootPath,
                    RecentWorkspaceKind.ProjectWorkspace);
                recents = new[] { seed };
            }

            RecentWorkspaces.Clear();
            foreach (var item in recents)
            {
                RecentWorkspaces.Add(item);
            }

            ApplyRecentWorkspacesFilter();
            NotifyRecentWorkspaceStats();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CSharpManagerViewModel] Failed to load recent workspaces: {ex.Message}");
        }
    }

    public void ApplyRecentWorkspacesFilter()
    {
        var query = SearchQuery?.Trim() ?? string.Empty;
        var filter = SelectedTypeFilter ?? "All";

        var matches = RecentWorkspaces.AsEnumerable();

        if (!string.IsNullOrEmpty(query))
        {
            matches = matches.Where(w =>
                w.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                w.Path.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                w.DisplayPath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (w.GitBranch != null && w.GitBranch.Contains(query, StringComparison.OrdinalIgnoreCase)));
        }

        if (string.Equals(filter, "Workspaces", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(filter, "Workspace", StringComparison.OrdinalIgnoreCase))
        {
            matches = matches.Where(w => w.IsProjectWorkspace);
        }
        else if (string.Equals(filter, "Notebooks", StringComparison.OrdinalIgnoreCase))
        {
            matches = matches.Where(w => w.IsNotebook);
        }
        else if (string.Equals(filter, "Scripts", StringComparison.OrdinalIgnoreCase))
        {
            matches = matches.Where(w => w.IsScript);
        }
        else if (string.Equals(filter, "Pinned", StringComparison.OrdinalIgnoreCase))
        {
            matches = matches.Where(w => w.IsPinned);
        }

        var ordered = matches
            .OrderByDescending(w => w.IsPinned)
            .ThenByDescending(w => w.LastOpenedUtc)
            .ToList();

        FilteredWorkspaces.Clear();
        foreach (var item in ordered)
        {
            FilteredWorkspaces.Add(item);
        }

        NotifyRecentWorkspaceStats();
    }

    private void NotifyRecentWorkspaceStats()
    {
        OnPropertyChanged(nameof(TotalRecentWorkspaces));
        OnPropertyChanged(nameof(RecentWorkspacesCount));
        OnPropertyChanged(nameof(RecentNotebooksCount));
        OnPropertyChanged(nameof(RecentScriptsCount));
        OnPropertyChanged(nameof(RecentPinnedCount));
        OnPropertyChanged(nameof(HasFilteredWorkspaces));
        OnPropertyChanged(nameof(ShowRecentWorkspaces));
        OnPropertyChanged(nameof(ShowRecentEmptyState));
        OnPropertyChanged(nameof(IsRecentWorkspacesEmpty));
    }

    [RelayCommand]
    public async Task OpenRecentWorkspaceAsync(RecentWorkspaceItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Path)) return;
        await OpenExistingProjectAsync(item.Path);
    }

    [RelayCommand]
    public async Task TogglePinRecentWorkspaceAsync(RecentWorkspaceItem? item)
    {
        if (item == null) return;
        await _storageService.RecentWorkspaces.TogglePinAsync(item.Id);
        item.IsPinned = !item.IsPinned;
        ApplyRecentWorkspacesFilter();
    }

    [RelayCommand]
    public async Task RemoveRecentWorkspaceAsync(RecentWorkspaceItem? item)
    {
        if (item == null) return;
        await _storageService.RecentWorkspaces.RemoveRecentAsync(item.Id);
        RecentWorkspaces.Remove(item);
        ApplyRecentWorkspacesFilter();
    }

    [RelayCommand]
    public void RevealWorkspaceInFinder(RecentWorkspaceItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Path)) return;
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var folder = item.IsProjectWorkspace ? item.Path : Path.GetDirectoryName(item.Path) ?? item.Path;
                Process.Start("open", $"\"{folder}\"");
            }
            else if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = item.IsProjectWorkspace ? $"\"{item.Path}\"" : $"/select,\"{item.Path}\"",
                    UseShellExecute = true
                });
            }
            else if (OperatingSystem.IsLinux())
            {
                var folder = item.IsProjectWorkspace ? item.Path : Path.GetDirectoryName(item.Path) ?? item.Path;
                Process.Start("xdg-open", $"\"{folder}\"");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CSharpManagerViewModel] Failed to reveal workspace: {ex.Message}");
        }
    }

    [RelayCommand]
    public void ResetRecentSearchAndFilters()
    {
        SearchQuery = string.Empty;
        SelectedTypeFilter = "All";
        ApplyRecentWorkspacesFilter();
    }
}
